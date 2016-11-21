using System;

namespace Test
{
    class Program
    {
        static int Main(string[] args)
        {
            var z = new Program();
            int x = 5;
            int y = 1;
            while (x > 0) {
                y *= x;
                x -= 1;
            }
            return y;
        }
    }
}
