using System.Text;

namespace UnmanagedStringDnlib
{
    internal static class NameGenerator
    {
        private static int _counter = 0;

        public static string Next()
        {
            int value = _counter++;
            StringBuilder sb = new StringBuilder();

            do
            {
                sb.Insert(0, (char)('A' + (value % 26)));
                value = (value / 26) - 1;
            }
            while (value >= 0);

            return sb.ToString();
        }
    }
}