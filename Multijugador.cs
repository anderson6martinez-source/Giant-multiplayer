using System;
using Engine;
using GameEntitySystem;

namespace GiganteCode
{
    // Punto unico para (1) saber quien manda la logica del mundo y (2) enterarse de que se cargo un mundo.
    public static class Multijugador
    {
        // ---- Quien manda ----
        //  true  = este proceso decide (solitario, anfitrion o servidor): botin, lanzas, spawns, IA, efectos.
        //  false = cliente remoto: solo muestra lo que el servidor le manda.
        // CommonLib.Net es el NetNode actual de SCNET 2.4; IsServer es true en el servidor.
        // Sin red o sin conexion (solitario / pantalla dividida) se considera autoridad.
        public static bool EsAutoridad()
        {
            try
            {
                Game.NetWork.NetNode red = Game.NetWork.CommonLib.Net;
                return red == null || !red.IsConnected || red.IsServer;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ---- Aviso de mundo cargado ----
        // SCNET no tiene los hooks OnProjectLoaded / OnProjectDisposed. El unico punto fiable por mundo es
        // InitializeCreatureTypes (GiganteSpawn lo llama) y desde ahi se avisa a los demas cargadores.
        // Cada cargador limpia su estado anterior al recibir el aviso, asi que no hace falta un aviso de cierre.
        public static Project Proyecto { get; private set; }

        public static event Action<Project> ProyectoCargado;

        public static void AlCargarProyecto(Project proyecto)
        {
            Proyecto = proyecto;
            Action<Project> aviso = ProyectoCargado;
            if (aviso == null)
            {
                return;
            }
            foreach (Delegate d in aviso.GetInvocationList())
            {
                try
                {
                    ((Action<Project>)d)(proyecto);
                }
                catch (Exception e)
                {
                    Log.Warning("Multijugador: error al cargar " + d.Method.DeclaringType.Name + " (" + e.Message + ")");
                }
            }
        }
    }
}
