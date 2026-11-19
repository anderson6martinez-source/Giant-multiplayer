using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;

namespace GiganteCode
{
    /// <summary>
    /// Genera refugios de madera dispersos por el mundo y los puebla con indigenas normales (y, a veces, chaman, jefe o cazador).
    /// Para API 1.8 - 1.9. No modifica la aparicion normal de los indigenas ni sus estadisticas.
    /// </summary>
    public class RefugioIndigenaSpawnLoader : ModLoader
    {
        // ---------------- Configuracion ----------------
        private const float Frecuencia = 0.06f;               // probabilidad por chunk nuevo (~1 de cada 16 chunks)
        private const string PlantillaChaman = "Chaman";     // Name de la plantilla del chaman en el .xdb
        private const float ProbabilidadChaman = 0.25f;      // probabilidad de que un refugio tenga un chaman
        private const string PlantillaJefe = "JefeIndigena";  // Name de la plantilla del jefe en el .xdb
        private const float ProbabilidadJefe = 0.15f;        // probabilidad de que un refugio tenga un jefe indigena
        private const string PlantillaCazador = "Cazador";  // Name de la plantilla del cazador en el .xdb
        private const float ProbabilidadCazador = 0.35f;     // probabilidad de que un refugio tenga cazadores
        private const int CazadoresMin = 1;                  // cuantos cazadores (entre min y max)
        private const int CazadoresMax = 2;
        private const string PlantillaIndigena = "Indigena";  // Name de la plantilla del indigena normal en el .xdb
        private const int IndigenasMin = 3;
        private const int IndigenasMax = 5;
        private const int MaxVariacionTerreno = 3;            // desnivel maximo permitido bajo el refugio
        private const float DistanciaActivacion = 60f;        // los indigenas aparecen cuando el jugador esta a menos de esto
        private const float RadioComprobacion = 20f;          // si ya hay indigenas a este radio, no se agregan mas

        // ---- Cofre con botin ----
        private const float ProbabilidadCofre = 0.5f;         // la mitad de los refugios tiene un cofre
        private const int DireccionCofre = 0;                 // hacia donde mira el cofre (0 a 3); cambia si queda de espaldas
        private const int IntentosMax = 5;                 // intentos de llenar el cofre si su inventario aun no existe

        // Botin: nombre del bloque, minimo, maximo, probabilidad (0 a 1). Los nombres que no existan se ignoran.
        private struct Botin
        {
            public string Nombre; public int Min; public int Max; public float Prob;
            public Botin(string n, int min, int max, float p) { Nombre = n; Min = min; Max = max; Prob = p; }
        }
        private static readonly Botin[] TablaBotin =
        {
            new Botin("RawMeatBlock",       1, 3, 0.80f),  // carne (comun)
            new Botin("LeatherBlock",       1, 3, 0.70f),  // cuero (comun)
            new Botin("StringBlock",        1, 2, 0.50f),  // cuerda
            new Botin("FeatherBlock",       1, 4, 0.40f),  // plumas (a veces)
            new Botin("WoodenSpearBlock",   1, 1, 0.30f),  // lanza de madera (a veces)
            new Botin("BaculoMagicoBlock",  1, 1, 0.10f),  // baculo magico (raro)
        };

        // Posicion y tamano del refugio dentro del chunk (16x16): queda siempre dentro del chunk
        private const int X0 = 4;
        private const int Z0 = 4;
        private const int Ancho = 8;
        private const int Fondo = 8;

        private struct Refugio
        {
            public int X;      // esquina del refugio (coordenadas del mundo)
            public int Z;
            public int Suelo;  // altura del bloque del piso
            public bool ConCofre;
        }

        private struct CofrePendiente
        {
            public Point3 Posicion;
            public int Intentos;
        }

        // Los chunks se generan en otro hilo: se guardan aqui y se procesan en el hilo principal
        private static readonly ConcurrentQueue<Refugio> RefugiosNuevos = new ConcurrentQueue<Refugio>();

        private readonly List<Refugio> pendientes = new List<Refugio>();
        private readonly HashSet<long> poblados = new HashSet<long>();
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
            try
            {
                GenerarEnChunk(chunk);
            }
            catch (Exception e)
            {
                Log.Warning("RefugioIndigena: error al generar en el chunk (" + e.Message + ")");
            }
        }

        private static void GenerarEnChunk(TerrainChunk chunk)
        {
            // Aleatorio determinista por chunk: si el chunk se regenera, sale el mismo refugio
            System.Random rng = new System.Random(
                unchecked(chunk.Coords.X * 73856093 ^ chunk.Coords.Y * 19349663 ^ 0x5BD1E995));
            if (rng.NextDouble() >= Frecuencia) return;

            // Altura del suelo en cada columna del terreno que ocupara el refugio
            int[,] suelos = new int[Ancho, Fondo];
            int minimo = int.MaxValue;
            int maximo = int.MinValue;
            for (int x = 0; x < Ancho; x++)
            {
                for (int z = 0; z < Fondo; z++)
                {
                    int y = BuscarSuelo(chunk, X0 + x, Z0 + z);
                    if (y < 2) return;                       // agua, hielo o sin suelo: no se construye
                    suelos[x, z] = y;
                    if (y < minimo) minimo = y;
                    if (y > maximo) maximo = y;
                }
            }
            if (maximo - minimo > MaxVariacionTerreno) return; // terreno demasiado irregular

            int suelo = maximo;                              // el piso queda a la altura del punto mas alto
            if (suelo + 8 >= TerrainChunk.Height) return;

            int tablones = PlanksBlock.Index;
            int madera = OakWoodBlock.Index;
            int adoquin = CobblestoneBlock.Index;
            int vidrio = GlassBlock.Index;

            // 1) Despejar el espacio (quita arboles y plantas) incluyendo el alero del techo
            for (int x = -1; x <= Ancho; x++)
                for (int z = -1; z <= Fondo; z++)
                    for (int y = suelo + 1; y <= suelo + 8; y++)
                        Poner(chunk, X0 + x, y, Z0 + z, 0);

            // 2) Cimientos de adoquin bajo las columnas mas bajas y piso de tablones
            for (int x = 0; x < Ancho; x++)
            {
                for (int z = 0; z < Fondo; z++)
                {
                    for (int y = suelos[x, z] + 1; y < suelo; y++)
                        Poner(chunk, X0 + x, y, Z0 + z, adoquin);
                    Poner(chunk, X0 + x, suelo, Z0 + z, tablones);
                }
            }

            // 3) Paredes de tablones (3 de alto)
            for (int y = 1; y <= 3; y++)
            {
                for (int x = 0; x < Ancho; x++)
                {
                    Poner(chunk, X0 + x, suelo + y, Z0, tablones);
                    Poner(chunk, X0 + x, suelo + y, Z0 + Fondo - 1, tablones);
                }
                for (int z = 0; z < Fondo; z++)
                {
                    Poner(chunk, X0, suelo + y, Z0 + z, tablones);
                    Poner(chunk, X0 + Ancho - 1, suelo + y, Z0 + z, tablones);
                }
            }

            // 4) Postes de madera en las esquinas
            int[] esquinasX = { X0, X0 + Ancho - 1 };
            int[] esquinasZ = { Z0, Z0 + Fondo - 1 };
            foreach (int x in esquinasX)
                foreach (int z in esquinasZ)
                    for (int y = 1; y <= 4; y++)
                        Poner(chunk, x, suelo + y, z, madera);

            // 5) Ventanas de vidrio
            int medioZ = Z0 + Fondo / 2;
            Poner(chunk, X0, suelo + 2, medioZ, vidrio);
            Poner(chunk, X0 + Ancho - 1, suelo + 2, medioZ, vidrio);
            Poner(chunk, X0 + Ancho / 2, suelo + 2, Z0 + Fondo - 1, vidrio);

            // 6) Techo: alero de tablones y dos escalones encima
            for (int x = -1; x <= Ancho; x++)
                for (int z = -1; z <= Fondo; z++)
                    Poner(chunk, X0 + x, suelo + 4, Z0 + z, tablones);

            for (int x = 1; x < Ancho - 1; x++)
                for (int z = 1; z < Fondo - 1; z++)
                    Poner(chunk, X0 + x, suelo + 5, Z0 + z, tablones);

            for (int x = 2; x < Ancho - 2; x++)
                for (int z = 2; z < Fondo - 2; z++)
                    Poner(chunk, X0 + x, suelo + 6, Z0 + z, tablones);

            // 7) Puerta (hueco de 1x2 al frente) y escalon de entrada
            int puertaX = X0 + Ancho / 2;
            Poner(chunk, puertaX, suelo + 1, Z0, 0);
            Poner(chunk, puertaX, suelo + 2, Z0, 0);
            Poner(chunk, puertaX, suelo, Z0 - 1, tablones);

            // 8) Interior: mesa de crafteo en una esquina y dos antorchas en el suelo
            Poner(chunk, X0 + 1, suelo + 1, Z0 + Fondo - 2, CraftingTableBlock.Index);
            int antorcha = Terrain.MakeBlockValue(TorchBlock.Index, 0, 4);
            PonerValor(chunk, X0 + 1, suelo + 1, Z0 + 1, antorcha);
            PonerValor(chunk, X0 + Ancho - 2, suelo + 1, Z0 + 1, antorcha);

            RefugiosNuevos.Enqueue(new Refugio
            {
                X = chunk.Origin.X + X0,
                Z = chunk.Origin.Y + Z0,
                Suelo = suelo,
                ConCofre = rng.NextDouble() < ProbabilidadCofre
            });
        }

        // Altura del primer bloque solido del suelo, ignorando arboles, hojas y plantas.
        // Devuelve -1 si hay agua o hielo (no se construye ahi).
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

        // ---------------- Indigenas (hilo principal) ----------------
        private void AlCargar(Project project)
        {
            AlCerrar();
            creaturas = project.FindSubsystem<SubsystemCreatureSpawn>(true);
            terreno = project.FindSubsystem<SubsystemTerrain>(true);
            jugadores = project.FindSubsystem<SubsystemPlayers>(true);
            tiempo = project.FindSubsystem<SubsystemTime>(true);
            Refugio descartado;
            while (RefugiosNuevos.TryDequeue(out descartado)) { }
            pendientes.Clear();
            poblados.Clear();
            cofresPendientes.Clear();
            proximaRevision = 0;
        }

        private void AlCerrar()
        {
            creaturas = null; terreno = null; jugadores = null; tiempo = null;
            Refugio descartado;
            while (RefugiosNuevos.TryDequeue(out descartado)) { }
            pendientes.Clear();
            poblados.Clear();
            cofresPendientes.Clear();
        }

        public override void SubsystemUpdate(float dt)
        {
            // Solo la autoridad puebla y llena cofres; el cliente descarta los avisos de generacion
            if (!Multijugador.EsAutoridad())
            {
                Refugio descartado;
                while (RefugiosNuevos.TryDequeue(out descartado)) { }
                return;
            }
            if (creaturas == null || terreno == null || jugadores == null || tiempo == null) return;

            LlenarCofresPendientes();

            Refugio nuevo;
            while (RefugiosNuevos.TryDequeue(out nuevo)) pendientes.Add(nuevo);
            if (pendientes.Count == 0) return;

            if (tiempo.GameTime < proximaRevision) return;
            proximaRevision = tiempo.GameTime + 1.0;

            // si el jugador se aleja volando muy rapido, no acumula refugios sin fin
            if (pendientes.Count > 64) pendientes.RemoveRange(0, pendientes.Count - 64);

            for (int i = pendientes.Count - 1; i >= 0; i--)
            {
                Refugio refugio = pendientes[i];
                long clave = ((long)refugio.X << 32) ^ (uint)refugio.Z;
                if (poblados.Contains(clave))
                {
                    pendientes.RemoveAt(i);
                    continue;
                }

                Vector3 centro = new Vector3(refugio.X + Ancho / 2f, refugio.Suelo + 1f, refugio.Z + Fondo / 2f);

                // solo cuando un jugador esta cerca (si no, los indigenas se perderian al descargarse)
                ComponentPlayer jugador = jugadores.FindNearestPlayer(centro);
                if (jugador == null || jugador.ComponentBody == null) continue;
                if (Vector3.Distance(jugador.ComponentBody.Position, centro) > DistanciaActivacion) continue;

                // el chunk tiene que estar completamente cargado
                TerrainChunk chunk = terreno.Terrain.GetChunkAtCell((int)centro.X, (int)centro.Z);
                if (chunk == null || chunk.State < TerrainChunkState.Valid) continue;

                poblados.Add(clave);
                pendientes.RemoveAt(i);

                // si ya hay indigenas aqui (mundo recargado o chunk regenerado), no se duplican
                if (ContarIndigenasCerca(centro) >= 2) continue;

                if (refugio.ConCofre) PonerCofre(refugio);

                int cantidad = random.Next(IndigenasMin, IndigenasMax + 1);
                for (int n = 0; n < cantidad; n++)
                {
                    Vector3 posicion = new Vector3(
                        refugio.X + 1 + random.Next(Ancho - 2) + 0.5f,
                        refugio.Suelo + 1.05f,
                        refugio.Z + 1 + random.Next(Fondo - 2) + 0.5f);
                    creaturas.SpawnCreature(PlantillaIndigena, posicion, false);
                }

                // a veces el refugio tiene un chaman en el centro
                if (random.NextDouble() < ProbabilidadChaman)
                {
                    Vector3 posChaman = new Vector3(refugio.X + Ancho / 2f, refugio.Suelo + 1.05f, refugio.Z + Fondo / 2f);
                    creaturas.SpawnCreature(PlantillaChaman, posChaman, false);
                }

                // y, mas raro, un jefe indigena (cerca de la puerta del fondo)
                if (random.NextDouble() < ProbabilidadJefe)
                {
                    Vector3 posJefe = new Vector3(refugio.X + Ancho / 2f + 1f, refugio.Suelo + 1.05f, refugio.Z + Fondo / 2f + 1f);
                    creaturas.SpawnCreature(PlantillaJefe, posJefe, false);
                }

                // a veces hay 1 o 2 cazadores con lanza (cerca de la entrada, lejos de la mesa, el cofre y las antorchas)
                if (random.NextDouble() < ProbabilidadCazador)
                {
                    int cazadores = random.Next(CazadoresMin, CazadoresMax + 1);
                    for (int c = 0; c < cazadores; c++)
                    {
                        Vector3 posCazador = new Vector3(refugio.X + 3.5f + 2f * c, refugio.Suelo + 1.05f, refugio.Z + 2.5f);
                        creaturas.SpawnCreature(PlantillaCazador, posCazador, false);
                    }
                }
            }
        }

        // ---------------- Cofre (hilo principal) ----------------
        // Esquina opuesta a la mesa de crafteo. ChangeCell hace que el juego cree la entidad del cofre.
        private void PonerCofre(Refugio refugio)
        {
            try
            {
                int x = refugio.X + Ancho - 2;
                int y = refugio.Suelo + 1;
                int z = refugio.Z + Fondo - 2;
                if (Terrain.ExtractContents(terreno.Terrain.GetCellValueFast(x, y, z)) != 0) return; // ocupado

                terreno.ChangeCell(x, y, z, Terrain.MakeBlockValue(ChestBlock.Index, 0, DireccionCofre));
                if (!LlenarCofre(x, y, z))
                    cofresPendientes.Add(new CofrePendiente { Posicion = new Point3(x, y, z), Intentos = 0 });
            }
            catch (Exception e)
            {
                Log.Warning("RefugioIndigena: no se pudo poner el cofre (" + e.Message + ")");
            }
        }

        private void LlenarCofresPendientes()
        {
            for (int i = cofresPendientes.Count - 1; i >= 0; i--)
            {
                CofrePendiente c = cofresPendientes[i];
                bool listo = false;
                try { listo = LlenarCofre(c.Posicion.X, c.Posicion.Y, c.Posicion.Z); }
                catch (Exception e) { Log.Warning("RefugioIndigena: error al llenar el cofre (" + e.Message + ")"); listo = true; }
                c.Intentos++;
                if (listo || c.Intentos >= IntentosMax) cofresPendientes.RemoveAt(i);
                else cofresPendientes[i] = c;
            }
        }

        // true si el cofre ya tiene su inventario (y quedo lleno); false si aun no existe
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
            // nunca queda vacio: si no salio nada, al menos cuero
            if (puestos == 0) PonerEnCofre(cofre, new Botin("LeatherBlock", 1, 2, 1f));
            return true;
        }

        private bool PonerEnCofre(ComponentChest cofre, Botin b)
        {
            Block bloque = BlocksManager.FindBlockByTypeName(b.Nombre, false);
            if (bloque == null) return false; // ese objeto no existe en esta version: se ignora

            int cantidad = random.Next(b.Min, b.Max + 1);
            int valor = Terrain.MakeBlockValue(bloque.BlockIndex, 0, 0);

            // casilla vacia al azar
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
