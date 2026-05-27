namespace MainClient.Extensions
{
    public static class ShuffleExtensions
    {
        private static readonly ThreadLocal<Random> _threadRnd = new(() => new Random());

        public static void Shuffle<T>(this T[] array)
        {
            var rnd = _threadRnd.Value!;
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (array[i], array[j]) = (array[j], array[i]);
            }
        }
    }

}
