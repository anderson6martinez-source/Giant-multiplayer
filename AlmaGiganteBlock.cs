using Engine;
using Engine.Graphics;
using Game;

namespace GiganteCode
{
    // Item "Alma de gigante". Lo sueltan los tres gigantes (1 por gigante).
    // Se referencia en el .xdb como "AlmaGiganteBlock;1;1".
    public class AlmaGiganteBlock : FlatBlock
    {
        // Indice unico del bloque. Si choca con otro mod, cambialo (maximo 1023).
        public const int Index = 700;

        // Imagen del item: Assets/Textures/Items/AlmaGigante.png dentro del mod.
        // La imagen esta en la casilla 0 (arriba a la izquierda) de una hoja de 16x16 casillas.
        private static Texture2D m_texture;

        public AlmaGiganteBlock()
        {
            DefaultTextureSlot = 0;
            // Nombre con el que se usa en las recetas (.cr)
            CraftingId = "almagigante";
        }

        public override void DrawBlock(
            PrimitivesRenderer3D primitivesRenderer, int value, Color color,
            float size, ref Matrix matrix, DrawBlockEnvironmentData environmentData)
        {
            if (m_texture == null)
            {
                m_texture = ContentManager.Get<Texture2D>("Textures/Items/AlmaGigante");
            }
            BlocksManager.DrawFlatBlock(
                primitivesRenderer, value, size, ref matrix, m_texture, color, true, environmentData);
        }

        public override string GetDisplayName(SubsystemTerrain subsystemTerrain, int value)
        {
            return "Alma de gigante";
        }

        public override string GetDescription(int value)
        {
            return "El alma de un gigante caido. Solo la sueltan los gigantes pequenos.";
        }

        public override string GetCategory(int value)
        {
            return "Items";
        }
    }
}
