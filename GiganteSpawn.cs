using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;

namespace GiganteCode
{
    // Registra el spawn natural de los gigantes (pequeno, canibal, muerto, de hielo y volador),
    // las nauseas, el resfriado, el lanzamiento de lanzas, la mutacion en luna llena y el vuelo del gigante volador.
    public class GiganteSpawnLoader : ModLoader
    {
        private static readonly System.Random s_random = new System.Random();

        // ---- Lanzas de madera ----
        private const float RangoMinimo = 3f;        // mas cerca que esto pelean cuerpo a cuerpo
        private const float RangoMaximo = 20f;       // distancia maxima de lanzamiento
        private const float VelocidadLanza = 22f;

        // ---- Mutacion del gigante muerto en luna llena (como el hombre lobo) ----
        // Fase de luna llena (0 a 7). Si en tu mundo la luna llena es otra fase, cambia este numero.
        private const int LunaLlena = 4;
        private static bool s_mutado;                 // true: es de noche y hay luna llena
        private static Texture2D s_texturaMuerto;
        private static Texture2D s_texturaMutante;
        private static Texture2D s_texturaOjos;       // gigante muerto con ojos rojos (de noche)

        // Gigantes que ya mutaron esta noche de luna llena / gigantes en plena transformacion
        private static readonly HashSet<Entity> s_mutadosVistos = new HashSet<Entity>();
        private static readonly HashSet<Entity> s_transformando = new HashSet<Entity>();

        // Gigantes muertos cuyo botin ya se solto
        private static readonly HashSet<Entity> s_botinEntregado = new HashSet<Entity>();

        private static int s_generacion;
        private const int TemperaturaMaxHielo = 4; // el gigante de hielo solo aparece si la temperatura del bioma es <= 4
        // Gigantes que ya lanzaron su unica lanza (no vuelven a tener hasta recargar el mundo)
        private static readonly HashSet<Entity> s_sinLanza = new HashSet<Entity>();

        // ---- Tareas con tiempo de juego (reemplazan a Task.Delay + Dispatcher) ----
        // Se procesan desde SubsystemUpdate: se pausan con el juego y no dependen de hilos.
        private class Efecto
        {
            public double Cuando;
            public System.Action Accion;
        }

        private class Transformacion
        {
            public Entity Entidad;
            public int Paso;
            public double Siguiente;
        }

        private static readonly List<Efecto> s_efectos = new List<Efecto>();
        private static readonly List<Transformacion> s_transformaciones = new List<Transformacion>();
        private static Project s_proyecto;
        private static SubsystemTime s_tiempo;
        private static double s_proximaRevision;
        private static bool s_texturasIntentadas;

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("InitializeCreatureTypes", this);
            ModsManager.RegisterHook("OnMinerHit", this);
            // Gigante volador
            Multijugador.ProyectoCargado -= AlCargar; // SCNET no tiene el hook OnProjectLoaded
            Multijugador.ProyectoCargado += AlCargar;
            ModsManager.RegisterHook("SubsystemUpdate", this);
        }

        // Nauseas: el gigante canibal y el muerto tienen 25% de causar nauseas al jugador.
        // Solo se aplica si el golpe realmente le quita vida: si la armadura lo absorbe, no pasa nada.
        public override void OnMinerHit(
            ComponentMiner miner, ComponentBody componentBody, Vector3 hitPoint, Vector3 hitDirection,
            ref float attackPower, ref float playerProbability, ref float creatureProbability,
            out bool Hitted)
        {
            Hitted = false;

            string atacante = miner.Entity.ValuesDictionary.DatabaseObject.Name;

            // Gigante de hielo: 20% de causar resfriado (solo si el golpe quita vida)
            if (atacante == "GiganteHielo")
            {
                Entity victima = componentBody.Entity;
                ComponentHealth saludVictima = victima.FindComponent<ComponentHealth>();
                ComponentFlu resfriado = victima.FindComponent<ComponentFlu>();
                if (saludVictima != null && resfriado != null
                    && victima.FindComponent<ComponentPlayer>() != null
                    && Multijugador.EsAutoridad()
                    && s_random.NextDouble() < 0.20)
                {
                    float vidaAntes = saludVictima.Health;
                    ProgramarEfecto(miner.Project, 0.15, () =>
                    {
                        if (saludVictima.Health < vidaAntes)
                        {
                            resfriado.StartFlu();
                        }
                    });
                }
            }

            if (atacante == "GigantePequenoCanibal" || atacante == "GigantePequenoMuerto")
            {
                Entity objetivo = componentBody.Entity;
                ComponentHealth salud = objetivo.FindComponent<ComponentHealth>();
                ComponentSickness nauseas = objetivo.FindComponent<ComponentSickness>();
                if (salud != null && nauseas != null
                    && objetivo.FindComponent<ComponentPlayer>() != null
                    && Multijugador.EsAutoridad()
                    && s_random.NextDouble() < (atacante == "GigantePequenoMuerto" && s_mutado ? 0.5 : 0.25))
                {
                    float saludAntes = salud.Health;
                    ProgramarEfecto(miner.Project, 0.15, () =>
                    {
                        if (salud.Health < saludAntes)
                        {
                            nauseas.StartSickness();
                        }
                    });
                }
            }
        }

        public override void InitializeCreatureTypes(
            SubsystemCreatureSpawn subsystemCreatureSpawn,
            List<SubsystemCreatureSpawn.CreatureType> creatureTypes)
        {
            SubsystemTerrain subsystemTerrain =
                subsystemCreatureSpawn.Project.FindSubsystem<SubsystemTerrain>(true);
            System.Random random = new System.Random();

            // nombre de plantilla, peso de spawn, min y max de criaturas por grupo
            AddGiant(subsystemCreatureSpawn, subsystemTerrain, creatureTypes, random,
                "GigantePequeno", 0.6f, 1, 1);
            AddGiant(subsystemCreatureSpawn, subsystemTerrain, creatureTypes, random,
                "GigantePequenoCanibal", 0.4f, 1, 1);
            AddGiant(subsystemCreatureSpawn, subsystemTerrain, creatureTypes, random,
                "GigantePequenoMuerto", 0.5f, 1, 2);
            // Gigante de hielo: solo aparece en biomas nevados y muy frios
            AddGiant(subsystemCreatureSpawn, subsystemTerrain, creatureTypes, random,
                "GiganteHielo", 0.5f, 1, 1, true);

            // Gigante volador: aparece de forma natural en cualquier bioma (sobre suelo solido).
            // Nace en el suelo y enseguida sube a volar (ver SubsystemUpdate).
            AddGiant(subsystemCreatureSpawn, subsystemTerrain, creatureTypes, random,
                PlantillaVolador, PesoVolador, 1, 1);

            // SCNET no tiene OnProjectLoaded: este es el punto por mundo. Avisa a todos los cargadores del mod
            // (volador, aldeas, refugios, esqueleto, chaman, cazador, lanzas y anciano).
            Multijugador.AlCargarProyecto(subsystemCreatureSpawn.Project);

            IniciarLanzas(subsystemCreatureSpawn.Project);
        }

        private static void AddGiant(
            SubsystemCreatureSpawn subsystemCreatureSpawn,
            SubsystemTerrain subsystemTerrain,
            List<SubsystemCreatureSpawn.CreatureType> creatureTypes,
            System.Random random,
            string templateName, float weight, int minCount, int maxCount, bool soloFrio = false)
        {
            SubsystemCreatureSpawn.CreatureType type =
                new SubsystemCreatureSpawn.CreatureType(
                    templateName, SpawnLocationType.Surface, true, false);

            // Adecuado si hay suelo solido debajo, aire donde aparece (2 bloques de alto) y no hay agua
            type.SpawnSuitabilityFunction = (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point) =>
            {
                int below = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y - 1, point.Z));
                int here = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y, point.Z));
                int above = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y + 1, point.Z));

                if (below == WaterBlock.Index || here == WaterBlock.Index || above == WaterBlock.Index)
                {
                    return 0f;
                }
                if (!BlocksManager.Blocks[below].IsCollidable)
                {
                    return 0f;
                }
                if (BlocksManager.Blocks[here].IsCollidable || BlocksManager.Blocks[above].IsCollidable)
                {
                    return 0f;
                }
                // Biomas nevados y muy frios: temperatura del bioma 0-4 (de 0 a 15)
                if (soloFrio && subsystemTerrain.Terrain.GetTemperature(point.X, point.Z) > TemperaturaMaxHielo)
                {
                    return 0f;
                }
                return weight;
            };

            type.SpawnFunction = (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point) =>
            {
                int count = random.Next(minCount, maxCount + 1);
                return subsystemCreatureSpawn.SpawnCreatures(creatureType, templateName, point, count).Count;
            };

            creatureTypes.Add(type);
        }

        // ---- Gigante volador: sin gravedad, patrulla en el aire y pica para atacar ----
        private const string NombreVolador = "Gigante volador";       // DisplayName del .xdb
        private const string PlantillaVolador = "GiganteVolador";     // Name de la plantilla del .xdb
        private const float PesoVolador = 0.45f;                      // peso de aparicion natural
        private const float AlturaCrucero = 6f;           // bloques sobre el suelo cuando no hay objetivo
        private const float VelocidadVertical = 3.5f;     // m/s maxima al subir o bajar
        private const float VelocidadPatrulla = 1.4f;     // m/s sin objetivo
        private const float VelocidadPersecucion = 2.78f; // 10 km/h
        private const float DistanciaAtaque = 2.6f;       // bloques
        private const float PausaAtaque = 1.3f;           // segundos entre golpes

        private SubsystemCreatureSpawn creaturas;
        private SubsystemTerrain terreno;
        private SubsystemTime tiempo;
        private readonly Dictionary<ComponentCreature, double> ultimoGolpe = new Dictionary<ComponentCreature, double>();
        private readonly Dictionary<ComponentCreature, Vector2> rumbo = new Dictionary<ComponentCreature, Vector2>();
        private readonly Dictionary<ComponentCreature, double> cambioRumbo = new Dictionary<ComponentCreature, double>();
        private readonly System.Random azar = new System.Random();

        private void AlCargar(Project proyecto)
        {
            AlCerrar();
            creaturas = proyecto.FindSubsystem<SubsystemCreatureSpawn>(true);
            terreno = proyecto.FindSubsystem<SubsystemTerrain>(true);
            tiempo = proyecto.FindSubsystem<SubsystemTime>(true);
            ultimoGolpe.Clear(); rumbo.Clear(); cambioRumbo.Clear();
        }

        private void AlCerrar()
        {
            creaturas = null; terreno = null; tiempo = null;
            ultimoGolpe.Clear(); rumbo.Clear(); cambioRumbo.Clear();
            s_proyecto = null; s_tiempo = null;
            s_efectos.Clear(); s_transformaciones.Clear(); s_transformando.Clear();
        }

        // ---------- vuelo ----------
        public override void SubsystemUpdate(float dt)
        {
            ActualizarTareas(); // revision de gigantes, transformaciones y efectos (tiempo de juego)
            if (!Multijugador.EsAutoridad()) return; // el vuelo y los golpes los decide el servidor
            if (creaturas == null || terreno == null || tiempo == null) return;
            if (dt <= 0f) return;
            if (dt > 0.1f) dt = 0.1f;

            foreach (ComponentCreature gigante in new List<ComponentCreature>(creaturas.Creatures))
            {
                if (gigante.DisplayName != NombreVolador) continue;

                ComponentBody cuerpo = gigante.ComponentBody;
                ComponentHealth salud = gigante.ComponentHealth;
                if (cuerpo == null || salud == null) continue;

                if (salud.Health <= 0f)
                {
                    cuerpo.IsGravityEnabled = true; // al morir cae
                    continue;
                }
                cuerpo.IsGravityEnabled = false;

                Vector3 pos = cuerpo.Position;
                int suelo = terreno.Terrain.GetTopHeight(Terrain.ToCell(pos.X), Terrain.ToCell(pos.Z)) + 1;

                ComponentChaseBehavior persecucion = gigante.Entity.FindComponent<ComponentChaseBehavior>();
                ComponentCreature objetivo = persecucion != null ? persecucion.Target : null;
                bool hayObjetivo = objetivo != null && objetivo.ComponentBody != null
                    && objetivo.ComponentHealth != null && objetivo.ComponentHealth.Health > 0f;

                float alturaDeseada;
                Vector2 direccion = Vector2.Zero;
                float velocidad = 0f;

                if (hayObjetivo)
                {
                    Vector3 posObj = objetivo.ComponentBody.Position;
                    Vector2 hacia = new Vector2(posObj.X - pos.X, posObj.Z - pos.Z);
                    float distHoriz = hacia.Length();
                    if (distHoriz > 0.01f) direccion = hacia / distHoriz;

                    if (distHoriz > 7f) alturaDeseada = System.Math.Max(suelo + 3f, posObj.Y + 3f);
                    else alturaDeseada = posObj.Y + 0.6f; // pica hacia el objetivo
                    velocidad = distHoriz > 1.2f ? VelocidadPersecucion : 0f;

                    // golpe cuando esta cerca
                    float distTotal = Vector3.Distance(pos + new Vector3(0f, cuerpo.BoxSize.Y * 0.5f, 0f),
                        posObj + new Vector3(0f, objetivo.ComponentBody.BoxSize.Y * 0.5f, 0f));
                    double ahora = tiempo.GameTime;
                    double ultimo;
                    if (!ultimoGolpe.TryGetValue(gigante, out ultimo)) ultimo = -100;
                    if (distTotal <= DistanciaAtaque && ahora - ultimo >= PausaAtaque)
                    {
                        ComponentMiner minero = gigante.Entity.FindComponent<ComponentMiner>();
                        if (minero != null)
                        {
                            Vector3 punto = posObj + new Vector3(0f, objetivo.ComponentBody.BoxSize.Y * 0.5f, 0f);
                            Vector3 dir = Vector3.Normalize(punto - pos);
                            minero.Hit(objetivo.ComponentBody, punto, dir);
                            ultimoGolpe[gigante] = ahora;
                        }
                    }
                }
                else
                {
                    alturaDeseada = suelo + AlturaCrucero;
                    double ahora = tiempo.GameTime;
                    double siguiente;
                    if (!cambioRumbo.TryGetValue(gigante, out siguiente) || ahora >= siguiente || !rumbo.ContainsKey(gigante))
                    {
                        double ang = azar.NextDouble() * System.Math.PI * 2.0;
                        rumbo[gigante] = new Vector2((float)System.Math.Cos(ang), (float)System.Math.Sin(ang));
                        cambioRumbo[gigante] = ahora + 4.0 + azar.NextDouble() * 6.0;
                    }
                    direccion = rumbo[gigante];
                    velocidad = VelocidadPatrulla;
                }

                alturaDeseada = System.Math.Max(alturaDeseada, suelo + 0.3f);

                // velocidad vertical suave hacia la altura deseada
                float vy = (alturaDeseada - pos.Y) * 1.5f;
                if (vy > VelocidadVertical) vy = VelocidadVertical;
                if (vy < -VelocidadVertical) vy = -VelocidadVertical;

                Vector3 v = cuerpo.Velocity;
                float k = System.Math.Min(1f, 4f * dt);
                v.Y += (vy - v.Y) * k;
                v.X += (direccion.X * velocidad - v.X) * k;
                v.Z += (direccion.Y * velocidad - v.Z) * k;
                cuerpo.Velocity = v;

                // girar hacia donde se mueve
                if (velocidad > 0.2f)
                {
                    float objetivoYaw = (float)System.Math.Atan2(-direccion.X, -direccion.Y);
                    Vector3 f = cuerpo.Matrix.Forward;
                    float yawActual = (float)System.Math.Atan2(-f.X, -f.Z);
                    float dif = objetivoYaw - yawActual;
                    while (dif > System.Math.PI) dif -= (float)(2 * System.Math.PI);
                    while (dif < -System.Math.PI) dif += (float)(2 * System.Math.PI);
                    float nuevo = yawActual + dif * System.Math.Min(1f, 6f * dt);
                    cuerpo.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, nuevo);
                }
            }
        }

        // ---- Lanzamiento de lanzas ----
        // Cada medio segundo revisa a los gigantes que persiguen a alguien y, si el objetivo esta
        // a distancia, le lanzan su unica lanza de madera. El gigante muerto no tiene lanza.
        // La lanza queda en el suelo y el jugador puede recogerla.

        private static void IniciarLanzas(Project project)
        {
            s_generacion++;
            s_sinLanza.Clear();
            s_botinEntregado.Clear();
            s_mutadosVistos.Clear();
            s_transformando.Clear();
            s_transformaciones.Clear();
            s_efectos.Clear();
            s_proyecto = project;
            s_tiempo = project.FindSubsystem<SubsystemTime>(true);
            s_proximaRevision = 0;
        }

        private static void ProgramarEfecto(Project project, double segundos, System.Action accion)
        {
            SubsystemTime reloj = project.FindSubsystem<SubsystemTime>(true);
            s_efectos.Add(new Efecto { Cuando = reloj.GameTime + segundos, Accion = accion });
        }

        // Se llama en cada SubsystemUpdate. Todo va con el tiempo del juego (GameTime).
        private static void ActualizarTareas()
        {
            if (s_proyecto == null || s_tiempo == null)
            {
                return;
            }
            double ahora = s_tiempo.GameTime;
            if (s_proximaRevision > ahora + 5.0)
            {
                s_proximaRevision = 0; // el reloj se reinicio (mundo nuevo)
            }

            // efectos pendientes (nauseas, resfriado)
            for (int i = s_efectos.Count - 1; i >= 0; i--)
            {
                if (i >= s_efectos.Count || s_efectos[i].Cuando > ahora)
                {
                    continue;
                }
                Efecto efecto = s_efectos[i];
                s_efectos.RemoveAt(i);
                try
                {
                    efecto.Accion();
                }
                catch (System.Exception)
                {
                }
            }

            // transformaciones en curso (un paso cada 80 ms)
            for (int i = s_transformaciones.Count - 1; i >= 0; i--)
            {
                Transformacion t = s_transformaciones[i];
                if (ahora < t.Siguiente)
                {
                    continue;
                }
                t.Siguiente = ahora + 0.08;
                if (!AvanzarTransformacion(t))
                {
                    s_transformaciones.RemoveAt(i);
                }
            }

            // revision de gigantes cada medio segundo
            if (ahora >= s_proximaRevision)
            {
                s_proximaRevision = ahora + 0.5;
                try
                {
                    RevisarGigantes(s_proyecto);
                }
                catch (System.Exception)
                {
                    s_proyecto = null; // el mundo se cerro: se detiene el ciclo
                }
            }
        }

        private static void RevisarGigantes(Project project)
        {
            SubsystemProjectiles proyectiles = project.FindSubsystem<SubsystemProjectiles>(true);
            SubsystemSky cielo = project.FindSubsystem<SubsystemSky>(true);
            bool noche = cielo.SkyLightIntensity < 0.3f;
            bool mutado = noche && cielo.MoonPhase == LunaLlena;
            s_mutado = mutado;

            // Solo la autoridad (servidor / anfitrion / solitario) suelta botin y lanza lanzas.
            // Las texturas y las animaciones corren en todos los jugadores.
            bool autoridad = Multijugador.EsAutoridad();

            if (!s_texturasIntentadas)
            {
                s_texturasIntentadas = true; // en un servidor sin graficos puede fallar: no se reintenta
                try
                {
                    s_texturaMuerto = ContentManager.Get<Texture2D>("Textures/Creatures/GigantePequenoMuerto");
                    s_texturaMutante = ContentManager.Get<Texture2D>("Textures/Creatures/GigantePequenoMutante");
                    s_texturaOjos = ContentManager.Get<Texture2D>("Textures/Creatures/GigantePequenoMuertoOjos");
                }
                catch (System.Exception)
                {
                }
            }

            foreach (Entity entidad in project.Entities)
            {
                string nombre = entidad.ValuesDictionary.DatabaseObject.Name;

                // Gigante muerto: de noche corre mas y pega mas fuerte. En luna llena MUTA:
                // textura de mutante (ojos rojos), mas vida, mas rapido y mas dano. Al amanecer vuelve a la normalidad.
                // Todo se recalcula cada vez, asi que no hace falta guardar estado.
                if (nombre == "GigantePequenoMuerto")
                {
                    ComponentLocomotion mov = entidad.FindComponent<ComponentLocomotion>();
                    ComponentMiner mano = entidad.FindComponent<ComponentMiner>();
                    ComponentHealth vida = entidad.FindComponent<ComponentHealth>();
                    ComponentCreatureModel modelo = entidad.FindComponent<ComponentCreatureModel>();

                    // Si acaba de morir, suelta su botin (normal o de mutado) una sola vez
                    if (vida != null && vida.Health <= 0f)
                    {
                        if (autoridad && s_botinEntregado.Add(entidad))
                        {
                            SoltarBotinMuerto(project, entidad, mutado);
                        }
                        continue;
                    }

                    // Transformacion: al llegar la luna llena cada gigante muta con una animacion
                    if (s_transformando.Contains(entidad))
                    {
                        continue; // la animacion controla su textura y su movimiento
                    }
                    if (mutado)
                    {
                        if (s_mutadosVistos.Add(entidad) && modelo != null && cuerpoDe(entidad) != null)
                        {
                            IniciarTransformacion(project, entidad, modelo);
                            continue;
                        }
                    }
                    else
                    {
                        s_mutadosVistos.Remove(entidad);
                    }

                    if (mov != null) mov.WalkSpeed = mutado ? 6.0f : (noche ? 4.6f : 2.2f);
                    if (mano != null) mano.AttackPower = mutado ? 9f : (noche ? 6f : 3f);
                    if (vida != null) vida.AttackResilience = mutado ? 600f : 150f;
                    if (modelo != null)
                    {
                        // Mutado: textura de mutante. De noche: ojos rojos. De dia: normal.
                        Texture2D textura = mutado ? s_texturaMutante : (noche ? s_texturaOjos : s_texturaMuerto);
                        if (modelo.TextureOverride != textura)
                        {
                            modelo.TextureOverride = textura;
                        }
                    }
                    continue;
                }

                // Solo el gigante normal y el canibal tienen lanza; el muerto la perdio
                if (nombre != "GigantePequeno" && nombre != "GigantePequenoCanibal" && nombre != "GiganteHielo")
                {
                    continue;
                }
                if (!autoridad || s_sinLanza.Contains(entidad))
                {
                    continue;
                }

                ComponentCreature gigante = entidad.FindComponent<ComponentCreature>();
                ComponentChaseBehavior persecucion = entidad.FindComponent<ComponentChaseBehavior>();
                if (gigante == null || persecucion == null || persecucion.Target == null)
                {
                    continue;
                }

                ComponentBody cuerpo = gigante.ComponentBody;
                ComponentBody cuerpoObjetivo = persecucion.Target.ComponentBody;
                Vector3 origen = cuerpo.Position + new Vector3(0f, cuerpo.BoxSize.Y * 0.75f, 0f);
                Vector3 destino = cuerpoObjetivo.Position + new Vector3(0f, cuerpoObjetivo.BoxSize.Y * 0.6f, 0f);
                Vector3 direccion = destino - origen;
                float distancia = direccion.Length();
                if (distancia < RangoMinimo || distancia > RangoMaximo)
                {
                    continue;
                }
                direccion /= distancia;

                // Compensa la caida por gravedad (aprox. 10 m/s^2) para que la lanza llegue al objetivo
                float tiempo = distancia / VelocidadLanza;
                Vector3 velocidad = direccion * VelocidadLanza;
                velocidad.Y += 5f * tiempo;

                // Lanza de madera (dato 0 = lanza de madera)
                int lanza = Terrain.MakeBlockValue(BlocksManager.FindBlockByTypeName("WoodenSpearBlock", true).BlockIndex, 0, 0);
                proyectiles.FireProjectile(lanza, origen + direccion * 1.2f, velocidad, Vector3.Zero, gigante);

                s_sinLanza.Add(entidad); // ya uso su unica lanza
            }
        }

        private static ComponentBody cuerpoDe(Entity entidad)
        {
            return entidad.FindComponent<ComponentBody>();
        }

        // ---- Transformacion en mutante (como el hombre lobo) ----
        // Dura unos 2.4 segundos: el gigante se detiene, ruge, tiembla, parpadea entre su
        // textura normal y la de mutante (cada vez mas rapido) y al final da un salto con otro rugido.
        private const int PasosTransformacion = 30;     // 30 pasos x 80 ms
        private const string SonidoRugido = "Audio/GigantePequeno/Normal/Idle/Grito_caverna";

        private static void IniciarTransformacion(Project project, Entity entidad, ComponentCreatureModel modelo)
        {
            ComponentBody cuerpo = entidad.FindComponent<ComponentBody>();
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(true);
            s_transformando.Add(entidad);
            audio.PlaySound(SonidoRugido, 1f, -0.6f, cuerpo.Position, 30f, true);
            s_transformaciones.Add(new Transformacion
            {
                Entidad = entidad,
                Paso = 0,
                Siguiente = s_tiempo.GameTime + 0.08
            });
        }

        // Un paso de la animacion. Devuelve true mientras la transformacion siga en curso.
        // La textura y los gestos se ven en todos; la velocidad (temblor y salto) solo la aplica la autoridad.
        private static bool AvanzarTransformacion(Transformacion t)
        {
            Entity entidad = t.Entidad;
            try
            {
                if (!entidad.IsAddedToProject)
                {
                    s_transformando.Remove(entidad);
                    return false;
                }
                ComponentBody cuerpo = entidad.FindComponent<ComponentBody>();
                ComponentCreatureModel modelo = entidad.FindComponent<ComponentCreatureModel>();
                ComponentHealth vida = entidad.FindComponent<ComponentHealth>();
                if (cuerpo == null || modelo == null || (vida != null && vida.Health <= 0f))
                {
                    s_transformando.Remove(entidad);
                    return false;
                }

                int paso = t.Paso;
                bool autoridad = Multijugador.EsAutoridad();

                if (paso >= PasosTransformacion)
                {
                    modelo.TextureOverride = s_texturaMutante;
                    modelo.AttackOrder = false;
                    s_transformando.Remove(entidad);
                    return false;
                }

                // Parpadeo entre normal y mutante: lento al principio, rapido al final
                bool mitad = paso >= PasosTransformacion / 2;
                bool verMutante = mitad ? (paso % 2 == 0) : (paso % 4 < 2);
                modelo.TextureOverride = verMutante ? s_texturaMutante : s_texturaOjos;

                // Se queda quieto, tiembla y mueve los brazos
                if (autoridad)
                {
                    Vector3 v = cuerpo.Velocity;
                    float temblor = 0.6f + paso * 0.08f;
                    cuerpo.Velocity = new Vector3(
                        (float)(s_random.NextDouble() - 0.5) * temblor, v.Y, (float)(s_random.NextDouble() - 0.5) * temblor);
                }
                modelo.AttackOrder = true;
                modelo.HeadShakeOrder = 1f;

                // Salto final con otro rugido
                if (paso == PasosTransformacion - 6)
                {
                    if (autoridad)
                    {
                        cuerpo.Velocity = new Vector3(0f, 6f, 0f);
                    }
                    SubsystemAudio audio = s_proyecto.FindSubsystem<SubsystemAudio>(true);
                    audio.PlaySound(SonidoRugido, 1f, -0.9f, cuerpo.Position, 40f, true);
                }

                t.Paso = paso + 1;
                return true;
            }
            catch (System.Exception)
            {
                s_transformando.Remove(entidad);
                return false;
            }
        }

        // ---- Botin del gigante muerto ----
        // El botin base del zombie (cuero 1-2, carne podrida 1-3, 1 alma) esta en el .xdb (Loot),
        // asi aparece en el bestiario. Cuando el gigante muere MUTADO se suelta aqui solo lo que falta
        // para llegar al botin del mutante: cuero 5-19, carne podrida 1-12 y 3 almas en total.
        private static void SoltarBotinMuerto(Project project, Entity entidad, bool mutado)
        {
            if (!mutado)
            {
                return;
            }
            SubsystemPickables objetos = project.FindSubsystem<SubsystemPickables>(true);
            ComponentBody cuerpo = entidad.FindComponent<ComponentBody>();
            Vector3 posicion = cuerpo.Position + new Vector3(0f, 0.8f, 0f);

            Soltar(objetos, LeatherBlock.Index, s_random.Next(4, 18), posicion);     // + 1-2 del xdb = 5-19
            Soltar(objetos, RottenMeatBlock.Index, s_random.Next(0, 10), posicion);  // + 1-3 del xdb = 1-12
            Soltar(objetos, AlmaGiganteBlock.Index, 2, posicion);                    // + 1 del xdb = 3
        }

        private static void Soltar(SubsystemPickables objetos, int bloque, int cantidad, Vector3 posicion)
        {
            if (cantidad <= 0)
            {
                return;
            }
            Vector3 velocidad = new Vector3(
                (float)(s_random.NextDouble() - 0.5) * 3f, 3f, (float)(s_random.NextDouble() - 0.5) * 3f);
            objetos.AddPickable(Terrain.MakeBlockValue(bloque), cantidad, posicion, velocidad, null);
        }
    }
}
