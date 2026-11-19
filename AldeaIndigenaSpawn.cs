using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;

namespace GiganteCode
{
    /// <summary>
    /// Aldea indigena: 4 chozas de madera alrededor de una plaza con fogata, todo dentro de UN chunk.
    /// Probabilidad: 6% por chunk nuevo. Gente: 1 jefe, 2 chamanes, 2-3 cazadores y 5-8 indigenas.
    /// Cofre con botin en la choza noroeste. Para API 1.8 - 1.9. Archivo nuevo: no modifica RefugioIndigenaSpawn.cs.
    /// </summary>
    public class AldeaIndigenaSpawnLoader : ModLoader
    {
        // ---------------- Configuracion ----------------
        private const float Frecuencia = 0.06f;               // 6% de los chunks nuevos tienen aldea
        // Los refugios usan el MISMO numero aleatorio por chunk: el refugio sale si es < RefugioFrecuencia
        // y la aldea si cae justo despues. Asi nunca coinciden en el mismo chunk.
        // Debe ser igual a "Frecuencia" de RefugioIndigenaSpawn.cs (0.06).
        private const float RefugioFrecuencia = 0.06f;

        private const string PlantillaIndigena = "Indigena";
        private const string PlantillaChaman = "Chaman";
        private const string PlantillaJefe = "JefeIndigena";
        private const string PlantillaCazador = "Cazador";
        private const int Chamanes = 2;                       // chamanes por aldea
        private const int Jefes = 1;
        private const int CazadoresMin = 2;
        private const int CazadoresMax = 3;
        private const int IndigenasMin = 5;
        private const int IndigenasMax = 8;

        private const bool ConEntradas = true;            // true = cada choza tiene una entrada (hueco de 1x2, sin puerta); false = chozas cerradas
        private const int MaxVariacionTerreno = 3;            // desnivel maximo permitido bajo la aldea
        private const float DistanciaActivacion = 60f;        // la gente aparece cuando el jugador esta a menos de esto
        private const float RadioComprobacion = 20f;          // si ya hay indigenas a este radio, no se agregan mas

        // ---- Cofre con botin (choza noroeste) ----
        private const int DireccionCofre = 0;                 // hacia donde mira el cofre (0 a 3); cambia si queda de espaldas
        private const int IntentosMax = 5;

        private struct Botin
        {
            public string Nombre; public int Min; public int Max; public float Prob;
            public Botin(string n, int min, int max, float p) { Nombre = n; Min = min; Max = max; Prob = p; }
        }
        private static readonly Botin[] TablaBotin =
        {
            new Botin("RawMeatBlock",       1, 3, 0.80f),
            new Botin("LeatherBlock",       1, 3, 0.70f),
            new Botin("StringBlock",        1, 2, 0.50f),
            new Botin("FeatherBlock",       1, 4, 0.40f),
            new Botin("WoodenSpearBlock",   1, 1, 0.30f),
            new Botin("BaculoMagicoBlock",  1, 1, 0.15f),  // baculo magico (un poco mas comun que en los refugios)
        };

        // Terreno que ocupa la aldea dentro del chunk (16x16): 14x14 desde (1,1)
        private const int X0 = 1;
        private const int Z0 = 1;
        private const int Lado = 14;

        private struct Aldea
        {
            public int X;      // esquina noroeste de la aldea (coordenadas del mundo)
            public int Z;
            public int Suelo;  // altura del piso
        }

        private struct CofrePendiente
        {
            public Point3 Posicion;
            public int Intentos;
        }

        // Los chunks se generan en otro hilo: se guardan aqui y se procesan en el hilo principal
        private static readonly ConcurrentQueue<Aldea> AldeasNuevas = new ConcurrentQueue<Aldea>();

        private readonly List<Aldea> pendientes = new List<Aldea>();
        private readonly HashSet<long> pobladas = new HashSet<long>();
        private readonly List<CofrePendiente> cofresPendientes = new List<CofrePendiente>();
        private readonly System.Random random = new System.Random();

        private SubsystemCreatureSpawn creaturas;
        private SubsystemTerrain terreno;
        private SubsystemPlayers jugadores;
        private SubsystemTime tiempo;
        private double proximaRevision;

        public override void __ModInitialize()
        {
            ModsManager.RegisterHook("OnTerrainContentsGenerated", this); // SCNET: hook por chunk
            Multijugador.ProyectoCargado -= AlCargar; // SCNET no tiene el hook OnProjectLoaded
            Multijugador.ProyectoCargado += AlCargar;
            ModsManager.RegisterHook("SubsystemUpdate", this);
        }

        // SCNET no tiene TerrainContentsGenerator24Initialize ni ChunkGenerationStep: usa el hook por chunk.
        // OJO: hay que comprobar que se llama durante la generacion (antes de que el chunk se envie).
        public override void OnTerrainContentsGenerated(TerrainChunk chunk)
        {
            GenerarEnChunkSeguro(chunk);
        }

        private static void GenerarEnChunkSeguro(TerrainChunk chunk)
        {
            try { GenerarEnChunk(chunk); }
            catch (Exception e) { Log.Warning("AldeaIndigena: error al generar en el chunk (" + e.Message + ")"); }
        }

        private static void GenerarEnChunk(TerrainChunk chunk)
        {
            // Mismo aleatorio por chunk que los refugios (misma semilla): la aldea ocupa el tramo siguiente
            System.Random rng = new System.Random(
                unchecked(chunk.Coords.X * 73856093 ^ chunk.Coords.Y * 19349663 ^ 0x5BD1E995));
            double r = rng.NextDouble();
            if (r < RefugioFrecuencia || r >= RefugioFrecuencia + Frecuencia) return;

            int[,] suelos = new int[Lado, Lado];
            int minimo = int.MaxValue;
            int maximo = int.MinValue;
            for (int x = 0; x < Lado; x++)
            {
                for (int z = 0; z < Lado; z++)
                {
                    int y = BuscarSuelo(chunk, X0 + x, Z0 + z);
                    if (y < 2) return;                       // agua, hielo o sin suelo
                    suelos[x, z] = y;
                    if (y < minimo) minimo = y;
                    if (y > maximo) maximo = y;
                }
            }
            if (maximo - minimo > MaxVariacionTerreno) return;

            int suelo = maximo;
            if (suelo + 9 >= TerrainChunk.Height) return;

            int madera = OakWoodBlock.Index;
            int adoquin = CobblestoneBlock.Index;
            int tierra = DirtBlock.Index;
            int antorcha = Terrain.MakeBlockValue(TorchBlock.Index, 0, 4);

            // 1) Despejar el espacio (arboles y plantas), incluido el alero de los techos
            for (int x = -1; x <= Lado; x++)
                for (int z = -1; z <= Lado; z++)
                    for (int y = suelo + 1; y <= suelo + 9; y++)
                        Poner(chunk, X0 + x, y, Z0 + z, 0);

            // 2) Rellenar los huecos hasta la altura del piso (tierra en el camino, adoquin bajo las chozas)
            for (int x = 0; x < Lado; x++)
            {
                for (int z = 0; z < Lado; z++)
                {
                    bool bajoChoza = EnChoza(x, z);
                    for (int y = suelos[x, z] + 1; y <= suelo; y++)
                        Poner(chunk, X0 + x, y, Z0 + z, bajoChoza ? adoquin : tierra);
                }
            }

            // 3) Las cuatro chozas (esquina, lado de la puerta): 0 = puerta al este, 1 = puerta al oeste
            Choza(chunk, 0, 0, suelo, 0);
            Choza(chunk, 9, 0, suelo, 1);
            Choza(chunk, 0, 9, suelo, 0);
            Choza(chunk, 9, 9, suelo, 1);

            // 4) Interior: mesa de crafteo en la choza noreste y una antorcha en cada choza
            Poner(chunk, X0 + 12, suelo + 1, Z0 + 1, CraftingTableBlock.Index);
            PonerValor(chunk, X0 + 1, suelo + 1, Z0 + 3, antorcha);                    // choza NO
            PonerValor(chunk, X0 + 10, suelo + 1, Z0 + 3, antorcha);                   // choza NE
            PonerValor(chunk, X0 + 1, suelo + 1, Z0 + 12, antorcha);                   // choza SO
            PonerValor(chunk, X0 + 10, suelo + 1, Z0 + 12, antorcha);                  // choza SE

            // 5) Plaza central: 4 postes con antorcha y una fogata de adoquin al centro
            int[] px = { 5, 8 };
            int[] pz = { 5, 8 };
            foreach (int cx in px)
            {
                foreach (int cz in pz)
                {
                    Poner(chunk, X0 + cx, suelo + 1, Z0 + cz, madera);
                    Poner(chunk, X0 + cx, suelo + 2, Z0 + cz, madera);
                    PonerValor(chunk, X0 + cx, suelo + 3, Z0 + cz, antorcha);
                }
            }
            for (int x = 6; x <= 7; x++)
                for (int z = 6; z <= 7; z++)
                    Poner(chunk, X0 + x, suelo, Z0 + z, adoquin);
            PonerValor(chunk, X0 + 6, suelo + 1, Z0 + 6, antorcha);
            PonerValor(chunk, X0 + 7, suelo + 1, Z0 + 7, antorcha);

            AldeasNuevas.Enqueue(new Aldea
            {
                X = chunk.Origin.X + X0,
                Z = chunk.Origin.Y + Z0,
                Suelo = suelo
            });
        }

        // true si la columna (x, z) de la aldea queda bajo una de las cuatro chozas (5x5 en las esquinas)
        private static bool EnChoza(int x, int z)
        {
            bool izq = x <= 4, der = x >= 9;
            bool arr = z <= 4, aba = z >= 9;
            return (izq || der) && (arr || aba);
        }

        // Choza de 5x5 con paredes de tablones, postes, techo escalonado, entrada opcional de 1x2 sin puerta y una ventana
        // lado: 0 = la puerta mira al este (+x), 1 = mira al oeste (-x)
        private static void Choza(TerrainChunk chunk, int hx, int hz, int suelo, int lado)
        {
            int tablones = PlanksBlock.Index;
            int madera = OakWoodBlock.Index;
            int vidrio = GlassBlock.Index;
            int bx = X0 + hx, bz = Z0 + hz;

            // piso
            for (int x = 0; x < 5; x++)
                for (int z = 0; z < 5; z++)
                    Poner(chunk, bx + x, suelo, bz + z, tablones);

            // paredes (3 de alto)
            for (int y = 1; y <= 3; y++)
            {
                for (int i = 0; i < 5; i++)
                {
                    Poner(chunk, bx + i, suelo + y, bz, tablones);
                    Poner(chunk, bx + i, suelo + y, bz + 4, tablones);
                    Poner(chunk, bx, suelo + y, bz + i, tablones);
                    Poner(chunk, bx + 4, suelo + y, bz + i, tablones);
                }
            }

            // postes de madera en las esquinas
            foreach (int cx in new[] { 0, 4 })
                foreach (int cz in new[] { 0, 4 })
                    for (int y = 1; y <= 4; y++)
                        Poner(chunk, bx + cx, suelo + y, bz + cz, madera);

            // techo: alero de 7x7, luego 5x5 y 3x3
            for (int x = -1; x <= 5; x++)
                for (int z = -1; z <= 5; z++)
                    Poner(chunk, bx + x, suelo + 4, bz + z, tablones);
            for (int x = 0; x <= 4; x++)
                for (int z = 0; z <= 4; z++)
                    Poner(chunk, bx + x, suelo + 5, bz + z, tablones);
            for (int x = 1; x <= 3; x++)
                for (int z = 1; z <= 3; z++)
                    Poner(chunk, bx + x, suelo + 6, bz + z, tablones);

            // entrada (hueco sin puerta) hacia la plaza y ventana en la pared contraria
            int paredPuerta = lado == 0 ? 4 : 0;
            int paredVentana = lado == 0 ? 0 : 4;
            if (ConEntradas)
            {
                Poner(chunk, bx + paredPuerta, suelo + 1, bz + 2, 0);
                Poner(chunk, bx + paredPuerta, suelo + 2, bz + 2, 0);
            }
            Poner(chunk, bx + paredVentana, suelo + 2, bz + 2, vidrio);
        }

        private static int BuscarSuelo(TerrainChunk chunk, int x, int z)
        {
            for (int y = TerrainChunk.Height - 2; y >= 2; y--)
            {
                int contents = Terrain.ExtractContents(chunk.GetCellValueFast(x, y, z));
                if (contents == 0) continue;
                if (contents == WaterBlock.Index || contents == IceBlock.Index) return -1;
                if (EsVegetacion(contents)) continue;

                Block bloque = BlocksManager.Blocks[contents];
                if (bloque == null || !bloque.IsCollidable) continue;
                return y;
            }
            return -1;
        }

        private static bool EsVegetacion(int contents)
        {
            return contents == OakWoodBlock.Index || contents == BirchWoodBlock.Index
                || contents == SpruceWoodBlock.Index || contents == OakLeavesBlock.Index
                || contents == BirchLeavesBlock.Index || contents == SpruceLeavesBlock.Index;
        }

        private static void Poner(TerrainChunk chunk, int x, int y, int z, int contents)
        {
            PonerValor(chunk, x, y, z, Terrain.MakeBlockValue(contents));
        }

        private static void PonerValor(TerrainChunk chunk, int x, int y, int z, int valor)
        {
            if (x < 0 || x >= TerrainChunk.Size ||
                z < 0 || z >= TerrainChunk.Size ||
                y < 1 || y >= TerrainChunk.Height)
            {
                return;
            }
            chunk.SetCellValueFast(x, y, z, valor);
        }

        // ---------------- Gente (hilo principal) ----------------
        private void AlCargar(Project project)
        {
            AlCerrar();
            creaturas = project.FindSubsystem<SubsystemCreatureSpawn>(true);
            terreno = project.FindSubsystem<SubsystemTerrain>(true);
            jugadores = project.FindSubsystem<SubsystemPlayers>(true);
            tiempo = project.FindSubsystem<SubsystemTime>(true);
            Aldea descartada;
            while (AldeasNuevas.TryDequeue(out descartada)) { }
            pendientes.Clear();
            pobladas.Clear();
            cofresPendientes.Clear();
            proximaRevision = 0;
        }

        private void AlCerrar()
        {
            creaturas = null; terreno = null; jugadores = null; tiempo = null;
            Aldea descartada;
            while (AldeasNuevas.TryDequeue(out descartada)) { }
            pendientes.Clear();
            pobladas.Clear();
            cofresPendientes.Clear();
        }

        public override void SubsystemUpdate(float dt)
        {
            // Solo la autoridad puebla y llena cofres; el cliente descarta los avisos de generacion
            if (!Multijugador.EsAutoridad())
            {
                Aldea descartado;
                while (AldeasNuevas.TryDequeue(out descartado)) { }
                return;
            }
            if (creaturas == null || terreno == null || jugadores == null || tiempo == null) return;

            LlenarCofresPendientes();

            Aldea nueva;
            while (AldeasNuevas.TryDequeue(out nueva)) pendientes.Add(nueva);
            if (pendientes.Count == 0) return;

            if (tiempo.GameTime < proximaRevision) return;
            proximaRevision = tiempo.GameTime + 1.0;

            if (pendientes.Count > 64) pendientes.RemoveRange(0, pendientes.Count - 64);

            for (int i = pendientes.Count - 1; i >= 0; i--)
            {
                Aldea aldea = pendientes[i];
                long clave = ((long)aldea.X << 32) ^ (uint)aldea.Z;
                if (pobladas.Contains(clave))
                {
                    pendientes.RemoveAt(i);
                    continue;
                }

                Vector3 centro = new Vector3(aldea.X + Lado / 2f, aldea.Suelo + 1f, aldea.Z + Lado / 2f);

                ComponentPlayer jugador = jugadores.FindNearestPlayer(centro);
                if (jugador == null || jugador.ComponentBody == null) continue;
                if (Vector3.Distance(jugador.ComponentBody.Position, centro) > DistanciaActivacion) continue;

                TerrainChunk chunk = terreno.Terrain.GetChunkAtCell((int)centro.X, (int)centro.Z);
                if (chunk == null || chunk.State < TerrainChunkState.Valid) continue;

                pobladas.Add(clave);
                pendientes.RemoveAt(i);

                // si ya hay indigenas aqui (mundo recargado o chunk regenerado), no se duplican
                if (ContarIndigenasCerca(centro) >= 2) continue;

                PonerCofre(aldea);

                for (int n = 0, total = random.Next(IndigenasMin, IndigenasMax + 1); n < total; n++)
                    creaturas.SpawnCreature(PlantillaIndigena, PosicionPlaza(aldea), false);
                for (int n = 0; n < Chamanes; n++)
                    creaturas.SpawnCreature(PlantillaChaman, PosicionPlaza(aldea), false);
                for (int n = 0; n < Jefes; n++)
                    creaturas.SpawnCreature(PlantillaJefe, PosicionPlaza(aldea), false);
                for (int n = 0, total = random.Next(CazadoresMin, CazadoresMax + 1); n < total; n++)
                    creaturas.SpawnCreature(PlantillaCazador, PosicionPlaza(aldea), false);
            }
        }

        // Casilla al azar de la plaza (4x4 en el centro), sin la fogata ni los postes
        private Vector3 PosicionPlaza(Aldea aldea)
        {
            while (true)
            {
                int cx = 5 + random.Next(4);
                int cz = 5 + random.Next(4);
                if (cx >= 6 && cx <= 7 && cz >= 6 && cz <= 7) continue;      // fogata
                if ((cx == 5 || cx == 8) && (cz == 5 || cz == 8)) continue;  // postes
                return new Vector3(aldea.X + cx + 0.5f, aldea.Suelo + 1.05f, aldea.Z + cz + 0.5f);
            }
        }

        // ---------------- Cofre (choza noroeste, hilo principal) ----------------
        private void PonerCofre(Aldea aldea)
        {
            try
            {
                int x = aldea.X + 1;
                int y = aldea.Suelo + 1;
                int z = aldea.Z + 1;
                if (Terrain.ExtractContents(terreno.Terrain.GetCellValueFast(x, y, z)) != 0) return;

                terreno.ChangeCell(x, y, z, Terrain.MakeBlockValue(ChestBlock.Index, 0, DireccionCofre));
                if (!LlenarCofre(x, y, z))
                    cofresPendientes.Add(new CofrePendiente { Posicion = new Point3(x, y, z), Intentos = 0 });
            }
            catch (Exception e)
            {
                Log.Warning("AldeaIndigena: no se pudo poner el cofre (" + e.Message + ")");
            }
        }

        private void LlenarCofresPendientes()
        {
            for (int i = cofresPendientes.Count - 1; i >= 0; i--)
            {
                CofrePendiente c = cofresPendientes[i];
                bool listo = false;
                try { listo = LlenarCofre(c.Posicion.X, c.Posicion.Y, c.Posicion.Z); }
                catch (Exception e) { Log.Warning("AldeaIndigena: error al llenar el cofre (" + e.Message + ")"); listo = true; }
                c.Intentos++;
                if (listo || c.Intentos >= IntentosMax) cofresPendientes.RemoveAt(i);
                else cofresPendientes[i] = c;
            }
        }

        private bool LlenarCofre(int x, int y, int z)
        {
            SubsystemBlockEntities entidades = terreno.Project.FindSubsystem<SubsystemBlockEntities>(true);
            ComponentBlockEntity bloque = entidades.GetBlockEntity(x, y, z);
            if (bloque == null) return false;
            ComponentChest cofre = bloque.Entity.FindComponent<ComponentChest>();
            if (cofre == null) return false;

            int puestos = 0;
            foreach (Botin b in TablaBotin)
            {
                if (random.NextDouble() >= b.Prob) continue;
                if (PonerEnCofre(cofre, b)) puestos++;
            }
            if (puestos == 0) PonerEnCofre(cofre, new Botin("LeatherBlock", 1, 2, 1f));
            return true;
        }

        private bool PonerEnCofre(ComponentChest cofre, Botin b)
        {
            Block bloque = BlocksManager.FindBlockByTypeName(b.Nombre, false);
            if (bloque == null) return false;

            int cantidad = random.Next(b.Min, b.Max + 1);
            int valor = Terrain.MakeBlockValue(bloque.BlockIndex, 0, 0);

            for (int intento = 0; intento < 20; intento++)
            {
                int casilla = random.Next(cofre.SlotsCount);
                if (cofre.GetSlotCount(casilla) != 0) continue;
                cofre.AddSlotItems(casilla, valor, cantidad);
                return true;
            }
            return false;
        }

        private int ContarIndigenasCerca(Vector3 centro)
        {
            int total = 0;
            foreach (ComponentCreature criatura in creaturas.Creatures)
            {
                if (criatura.ComponentBody == null) continue;
                if (criatura.Entity.ValuesDictionary.DatabaseObject.Name != PlantillaIndigena) continue;
                if (Vector3.Distance(criatura.ComponentBody.Position, centro) <= RadioComprobacion) total++;
            }
            return total;
        }
    }
}
