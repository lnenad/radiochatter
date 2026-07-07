using System;
using UnityEngine;

namespace RadioChatter.Speech
{
    internal static class NumberSpeech
    {
        private static readonly string[] DigitWords =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"
        };

        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        };

        private static readonly string[] Tens =
        {
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        public static string Bearing(float degrees)
        {
            int value = Mathf.RoundToInt(NormalizeHeading(degrees)) % 360;
            int hundreds = value / 100;
            int tens = (value / 10) % 10;
            int ones = value % 10;
            return $"{DigitWords[hundreds]} {DigitWords[tens]} {DigitWords[ones]}";
        }

        public static string HeadingRunway(float degrees)
        {
            int runway = Mathf.RoundToInt(NormalizeHeading(degrees) / 10f);
            if (runway == 0)
                runway = 36;

            return runway.ToString("00");
        }

        public static string Natural(float value)
        {
            return Natural(Mathf.RoundToInt(value));
        }

        public static string Natural(int value)
        {
            if (value < 0)
                return "minus " + Natural(-value);

            if (value < 20)
                return Ones[value];

            if (value < 100)
            {
                int ten = value / 10;
                int one = value % 10;
                return one == 0 ? Tens[ten] : $"{Tens[ten]} {Ones[one]}";
            }

            if (value < 1000)
            {
                int hundred = value / 100;
                int remainder = value % 100;
                return remainder == 0 ? $"{Ones[hundred]} hundred" : $"{Ones[hundred]} hundred {Natural(remainder)}";
            }

            if (value < 1000000)
            {
                int thousands = value / 1000;
                int remainder = value % 1000;
                return remainder == 0 ? $"{Natural(thousands)} thousand" : $"{Natural(thousands)} thousand {Natural(remainder)}";
            }

            return value.ToString();
        }

        private static float NormalizeHeading(float heading)
        {
            heading %= 360f;
            return heading < 0f ? heading + 360f : heading;
        }
    }
}
