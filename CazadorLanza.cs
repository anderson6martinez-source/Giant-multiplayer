// Cazador indigena (plantilla "Cazador" del .xdb): lanza su lanza de madera a distancia y pelea cuerpo a cuerpo de cerca.
//  - Solo lanza a quien lo ataco a el o a los suyos (igual que la maldicion del chaman: usa PazIndigena.PuedeAtacar).
//  - No lanza si hay un bloque en medio ni si hay un indigena, chaman, jefe u otro cazador en la linea de tiro
//    (asi no se hieren entre ellos ni se pelean por un lanzamiento perdido).
//  - Despues de lanzar, la lanza de su mano desaparece y tarda 8 segundos en hacer otra de madera (no puede lanzar antes). De cerca (menos de 4 bloques) ataca con la lanza en la mano (daño de la plantilla).
// Archivo nuevo: sube este .cs junto a los demas; se compila todo junto en GiganteCode.dll.
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using GameEntitySystem;
using GiganteCode;

namespace Game
{
    public class CazadorLoader : ModLoader
    {
        const string PlantillaCazador = "Cazador";
        const string BloqueLanza = "WoodenSpearBlock";   // lanza de madera (el mismo nombre que usa el botin del refugio)

        const float AlcanceMaximo = 18f;                 // bloques
        const float AlcanceMinimo = 4f;                  // mas cerca que esto pelea cuerpo a cuerpo
        const double Enfriamiento = 8.0;                 // segundos que tarda en fabricarse otra lanza de madera
        const float VelocidadLanza = 24f;                // bloques por segundo
        const float RadioAliado = 0.9f;                  // si un aliado pasa a menos de esto de la trayectoria, no lanza

        const double PausaRevision = 0.4;

        static readonly string[] HuesosLanza = { "LanzaMango", "LanzaPunta" };   // partes de la lanza en HumanoPequenoLanza.dae

        static readonly string[] Aliados = { "Indigena", "Chaman", "JefeIndigena", "Cazador" };

        SubsystemCreatureSpawn creaturas;
        SubsystemTerrain terreno;
        SubsystemTime tiempo;
        SubsystemProjectiles proyectiles;

        readonly Dictionary<ComponentCreature, double> proximoLanzamiento = new Dictionary<ComponentCreature, double>();
        readonly Dictionary<ComponentCreature, bool> lanzaOculta = new Dictionary<ComponentCreature, bool>();
        double proximaRevision;
        double proximaLimpieza;

        public override void __ModInitialize()
        {
            Multijugador.ProyectoCargado -= AlCargar; // SCNET no tiene el hook OnProjectLoaded
            Multijugador.ProyectoCargado += AlCargar;
            ModsManager.RegisterHook("SubsystemUpdate", this);
        }

        private void AlCargar(Project proyecto)
        {
            AlCerrar();
            creaturas = proyecto.FindSubsystem<SubsystemCreatureSpawn>(true);
            terreno = proyecto.FindSubsystem<SubsystemTerrain>(true);
            tiempo = proyecto.FindSubsystem<SubsystemTime>(true);
            proyectiles = proyecto.FindSubsystem<SubsystemProjectiles>(true);
            proximoLanzamiento.Clear();
            lanzaOculta.Clear();
            proximaRevision = 0;
            proximaLimpieza = 0;
        }

        private void AlCerrar()
        {
            creaturas = null; terreno = null; tiempo = null; proyectiles = null;
            proximoLanzamiento.Clear();
            lanzaOculta.Clear();
        }

        static bool EstaViva(ComponentCreature c)
        {
            return c != null && c.ComponentHealth != null && c.ComponentBody != null && c.ComponentHealth.Health > 0f;
        }

        static string Plantilla(ComponentCreature c)
        {
            return c.Entity.ValuesDictionary.DatabaseObject.Name;
        }

        static bool EsAliado(ComponentCreature c)
        {
            string p = Plantilla(c);
            for (int i = 0; i < Aliados.Length; i++) if (Aliados[i] == p) return true;
            return false;
        }

        static float DistanciaASegmento(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float largo2 = ab.LengthSquared();
            if (largo2 < 0.0001f) return Vector3.Distance(p, a);
            float t = MathUtils.Clamp(Vector3.Dot(p - a, ab) / largo2, 0f, 1f);
            return Vector3.Distance(p, a + ab * t);
        }

        public override void SubsystemUpdate(float dt)
        {
            if (creaturas == null || tiempo == null) return;
            if (!Multijugador.EsAutoridad()) return; // lanzas, ataques y recogidas las decide el servidor
            if (tiempo.GameTime < proximaRevision) return;
            proximaRevision = tiempo.GameTime + PausaRevision;

            foreach (ComponentCreature cazador in creaturas.Creatures)
            {
                if (!EstaViva(cazador) || Plantilla(cazador) != PlantillaCazador) continue;
                RecuperarLanza(cazador);
                Lanzar(cazador);
            }

            // limpieza de cazadores que ya no existen (cada ~10 s)
            if (tiempo.GameTime >= proximaLimpieza)
            {
                proximaLimpieza = tiempo.GameTime + 10.0;
                var vivos = new HashSet<ComponentCreature>(creaturas.Creatures);
                var viejos = new List<ComponentCreature>();
                foreach (ComponentCreature c in proximoLanzamiento.Keys) if (!vivos.Contains(c)) viejos.Add(c);
                foreach (ComponentCreature c in viejos) { proximoLanzamiento.Remove(c); lanzaOculta.Remove(c); }
            }
        }

        // Muestra u oculta la lanza que lleva en la mano (oculta = escala casi cero; null = vuelve a su posicion normal)
        static void MostrarLanza(ComponentCreature cazador, bool visible)
        {
            ComponentCreatureModel modelo = cazador.ComponentCreatureModel;
            if (modelo == null || modelo.Model == null) return;
            foreach (string nombre in HuesosLanza)
            {
                ModelBone hueso = modelo.Model.FindBone(nombre, false);
                if (hueso == null) continue;
                modelo.SetBoneTransform(hueso.Index, visible ? (Matrix?)null : Matrix.CreateScale(0.0001f));
            }
        }

        // Pasados los 8 segundos vuelve a tener su lanza en la mano
        void RecuperarLanza(ComponentCreature cazador)
        {
            if (!lanzaOculta.TryGetValue(cazador, out bool oculta) || !oculta) return;
            proximoLanzamiento.TryGetValue(cazador, out double listo);
            if (tiempo.GameTime < listo) return;
            MostrarLanza(cazador, true);
            lanzaOculta[cazador] = false;
        }

        void Lanzar(ComponentCreature cazador)
        {
            double ahora = tiempo.GameTime;
            proximoLanzamiento.TryGetValue(cazador, out double listo);
            if (ahora < listo) return;

            ComponentChaseBehavior persecucion = cazador.Entity.FindComponent<ComponentChaseBehavior>();
            ComponentCreature objetivo = persecucion != null ? persecucion.Target : null;
            if (!EstaViva(objetivo)) return;

            // nunca a un aliado, y solo a quien lo ataco a el o a los suyos
            if (EsAliado(objetivo)) return;
            if (!PazIndigena.PuedeAtacar(cazador, objetivo, ahora)) return;

            Block bloque = BlocksManager.FindBlockByTypeName(BloqueLanza, false);
            if (bloque == null) return; // esa lanza no existe en esta version

            Vector3 origen = cazador.ComponentBody.Position + new Vector3(0f, cazador.ComponentBody.BoxSize.Y * 0.8f, 0f);
            Vector3 destino = objetivo.ComponentBody.Position + new Vector3(0f, objetivo.ComponentBody.BoxSize.Y * 0.5f, 0f);
            float distancia = (destino - origen).Length();
            if (distancia > AlcanceMaximo || distancia < AlcanceMinimo) return;

            // sin linea de vista (un bloque en medio): no lanza todavia
            var choque = terreno.Raycast(origen, destino, false, true,
                (v, d) => BlocksManager.Blocks[Terrain.ExtractContents(v)].IsCollidable);
            if (choque.HasValue) return;

            // ningun aliado en la linea de tiro
            foreach (ComponentCreature otro in creaturas.Creatures)
            {
                if (otro == cazador || otro == objetivo || !EstaViva(otro) || !EsAliado(otro)) continue;
                Vector3 centro = otro.ComponentBody.Position + new Vector3(0f, otro.ComponentBody.BoxSize.Y * 0.5f, 0f);
                if (DistanciaASegmento(centro, origen, destino) < RadioAliado) return;
            }

            // apunta un poco mas arriba para compensar la caida de la lanza
            float tiempoVuelo = distancia / VelocidadLanza;
            Vector3 punto = destino + new Vector3(0f, 5f * tiempoVuelo * tiempoVuelo, 0f);
            Vector3 direccion = Vector3.Normalize(punto - origen);

            int valor = Terrain.MakeBlockValue(bloque.BlockIndex, 0, 0);
            proyectiles.FireProjectile(valor, origen + direccion * 0.8f, direccion * VelocidadLanza, Vector3.Zero, cazador);
            proximoLanzamiento[cazador] = ahora + Enfriamiento;
            MostrarLanza(cazador, false);   // se queda con la mano vacia hasta fabricar otra
            lanzaOculta[cazador] = true;

            if (cazador.ComponentCreatureModel != null) cazador.ComponentCreatureModel.AttackOrder = true; // mueve el brazo
        }
    }
}
