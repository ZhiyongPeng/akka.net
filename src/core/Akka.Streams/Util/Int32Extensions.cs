﻿namespace Akka.Streams.Util
{
    public static class Int32Extensions
    {

        // see http://stackoverflow.com/questions/10439242/count-leading-zeroes-in-an-int32
        internal static int NumberOfLeadingZeros(this int x)
        {
            x |= (x >> 1);
            x |= (x >> 2);
            x |= (x >> 4);
            x |= (x >> 8);
            x |= (x >> 16);

            x -= (x >> 1) & 0x55555555;
            x = ((x >> 2) & 0x33333333) + (x & 0x33333333);
            x = ((x >> 4) + x) & 0x0f0f0f0f;
            x += x >> 8;
            x += x >> 16;
            x = x & 0x0000003f; // number of ones

            return sizeof (int)*8 - x;
        }
    }
}