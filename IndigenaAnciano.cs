// Indigena anciano (plantilla "IndigenaAnciano" del .xdb): "Fue expulsado de la tribu hace mucho tiempo, odia a los indigenas".
//  - 30 de vida y 4.5 de dano (estan en el .xdb).
//  - Agresivo contra los indigenas: sale a cazar al indigena normal y al chaman mas cercano.
//    Como los indigenas se defienden entre ellos, al atacar a uno el grupo entero va contra el anciano.
//  - Es "LandOther" en el .xdb, asi que los depredadores lo ven como presa (igual que a los indigenas).
//  - No ataca al jugador por iniciativa propia: solo se defiende si el jugador lo golpea.
//  - Aparece solo (uno por vez) por el mundo, lejos de los refugios.
// Archivo nuevo e independiente: sube este .cs junto a los demas; se compila todo junto en GiganteCode.dll.
using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;

namespace Game
{
    public class IndigenaAncianoLoader : ModLoader
    {
        const string PlantillaAnciano = "IndigenaAnciano";

        // A quienes odia (Name de la plantilla en el .xdb). Agrega "JefeIndigena" o "Cazador" si tambien debe odiarlos.
        static readonly string[] Odiados = { "Indigena", "Chaman" };

        const float RadioCaza = 25f;          // a que distancia busca indigenas
        const float RangoPersecucion = 40f;   // hasta donde los persigue
        const float TiempoPersecucion = 40f;  // segundos maximos de persecucion
        const double PausaCaza = 1.0;         // cada cuanto busca victimas (segundos)
        const double PausaCalma = 0.1;        // cada cuanto revisa que no persiga al jugador sin motivo
        const double PermisoDefensa = 40.0;   // segundos que puede atacar al jugador despues de que este lo golpea

        // ---- aparicion ----
        const float PesoAparicion = 0.25f;    // frecuencia (los indigenas normales usan 0.5)

        SubsystemCreatureSpawn creaturas;
        SubsystemTime tiempo;
        double proximaCaza;
        double proximaCalma;

        readonly HashSet<Entity> suscritos = new HashSet<Entity>();
        // anciano -> hasta cuando puede atacar al jugador (porque el jugador lo golpeo)
        readonly Dictionary<ComponentCreature, double> provocado = new Dictionary<ComponentCreature, double>();

        static System.Reflection.MethodInfo metodoDetener;
        static bool detenerBuscado;

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("InitializeCreatureTypes", this);
            Multijugador.ProyectoCargado -= AlCargar; // SCNET no tiene el hook OnProjectLoaded
            Multijugador.ProyectoCargado += AlCargar;
            ModsManager.RegisterHook("SubsystemUpdate", this);
        }

        // ---------- aparicion ----------
        public override void InitializeCreatureTypes(
            SubsystemCreatureSpawn subsystemCreatureSpawn,
            List<SubsystemCreatureSpawn.CreatureType> creatureTypes)
        {
            SubsystemTerrain subsystemTerrain =
                subsystemCreatureSpawn.Project.FindSubsystem<SubsystemTerrain>(true);

            SubsystemCreatureSpawn.CreatureType type =
                new SubsystemCreatureSpawn.CreatureType(
                    PlantillaAnciano, SpawnLocationType.Surface, true, false);

            // suelo solido debajo, 2 bloques de aire y sin agua
            type.SpawnSuitabilityFunction = (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point) =>
            {
                int below = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y - 1, point.Z));
                int here = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y, point.Z));
                int above = Terrain.ExtractContents(
                    subsystemTerrain.Terrain.GetCellValueFast(point.X, point.Y + 1, point.Z));

                if (below == WaterBlock.Index || here == WaterBlock.Index || above == WaterBlock.Index) return 0f;
                if (!BlocksManager.Blocks[below].IsCollidable) return 0f;
                if (BlocksManager.Blocks[here].IsCollidable || BlocksManager.Blocks[above].IsCollidable) return 0f;
                return PesoAparicion;
            };

            // siempre solo, un unico anciano
            type.SpawnFunction = (SubsystemCreatureSpawn.CreatureType creatureType, Point3 point) =>
            {
                return subsystemCreatureSpawn.SpawnCreatures(creatureType, PlantillaAnciano, point, 1).Count;
            };

            creatureTypes.Add(type);
        }

        private void AlCargar(Project proyecto)
        {
            AlCerrar();
            creaturas = proyecto.FindSubsystem<SubsystemCreatureSpawn>(true);
            tiempo = proyecto.FindSubsystem<SubsystemTime>(true);
            proximaCaza = 0;
            proximaCalma = 0;
            suscritos.Clear();
            provocado.Clear();
        }

        private void AlCerrar()
        {
            creaturas = null; tiempo = null;
            suscritos.Clear();
            provocado.Clear();
        }

        static bool EstaViva(ComponentCreature c)
        {
            return c != null && c.ComponentHealth != null && c.ComponentHealth.Health > 0f && c.ComponentBody != null;
        }

        static string Plantilla(ComponentCreature c)
        {
            return c.Entity.ValuesDictionary.DatabaseObject.Name;
        }

        static bool EsJugador(ComponentCreature c)
        {
            return c != null && c.Entity.FindComponent<ComponentPlayer>() != null;
        }

        static void DetenerAtaque(ComponentChaseBehavior persecucion)
        {
            if (!detenerBuscado)
            {
                detenerBuscado = true;
                metodoDetener = typeof(ComponentChaseBehavior).GetMethod("StopAttack",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (metodoDetener == null)
                    Log.Warning("IndigenaAnciano: ComponentChaseBehavior no tiene StopAttack() en esta version del juego");
            }
            if (metodoDetener != null) metodoDetener.Invoke(persecucion, null);
        }

        public override void SubsystemUpdate(float dt)
        {
            if (creaturas == null || tiempo == null) return;
            if (!Multijugador.EsAutoridad()) return; // lanzas, ataques y recogidas las decide el servidor
            double ahora = tiempo.GameTime;
            bool calmar = ahora >= proximaCalma;
            bool cazar = ahora >= proximaCaza;
            if (!calmar && !cazar) return;
            if (calmar) proximaCalma = ahora + PausaCalma;
            if (cazar) proximaCaza = ahora + PausaCaza;

            foreach (ComponentCreature anciano in creaturas.Creatures)
            {
                if (!EstaViva(anciano) || Plantilla(anciano) != PlantillaAnciano) continue;

                // si el jugador lo golpea, a partir de ahora puede atacarlo (se suscribe una sola vez)
                if (suscritos.Add(anciano.Entity))
                {
                    ComponentCreature yo = anciano;
                    anciano.ComponentHealth.Injured += (Injury lesion) =>
                    {
                        if (tiempo != null && EsJugador(lesion.Attacker))
                            provocado[yo] = tiempo.GameTime + PermisoDefensa;
                    };
                }

                ComponentChaseBehavior persecucion = anciano.Entity.FindComponent<ComponentChaseBehavior>();
                if (persecucion == null) continue;

                // no persigue al jugador por iniciativa propia
                if (calmar && persecucion.Target != null && EsJugador(persecucion.Target))
                {
                    double hasta;
                    if (!provocado.TryGetValue(anciano, out hasta) || ahora >= hasta)
                        DetenerAtaque(persecucion);
                }

                // caza: si no tiene objetivo, va por el indigena mas cercano
                if (cazar && (persecucion.Target == null || !EstaViva(persecucion.Target)))
                {
                    ComponentCreature masCercano = null;
                    float mejor = RadioCaza;
                    foreach (ComponentCreature otro in creaturas.Creatures)
                    {
                        if (!EstaViva(otro) || Array.IndexOf(Odiados, Plantilla(otro)) < 0) continue;
                        float distancia = Vector3.Distance(anciano.ComponentBody.Position, otro.ComponentBody.Position);
                        if (distancia < mejor)
                        {
                            mejor = distancia;
                            masCercano = otro;
                        }
                    }
                    if (masCercano != null)
                        persecucion.Attack(masCercano, RangoPersecucion, TiempoPersecucion, false);
                }
            }

            // limpieza de ancianos que ya no existen
            if (cazar && provocado.Count > 0)
            {
                var vivos = new HashSet<ComponentCreature>(creaturas.Creatures);
                var viejos = new List<ComponentCreature>();
                foreach (ComponentCreature c in provocado.Keys) if (!vivos.Contains(c)) viejos.Add(c);
                foreach (ComponentCreature c in viejos) provocado.Remove(c);
            }
        }
    }
}
