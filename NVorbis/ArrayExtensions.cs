
namespace NVorbis
{
    public static class ArrayExtensions
    {
        public static int Max(this int[] array)
        {
            int max = int.MinValue;
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] > max)
                    max = array[i];
            }
            return max;
        }
    }
}
