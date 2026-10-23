using System;
using PhoneMicBridge.Cli;

namespace PhoneMicBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var options = CliOptions.Parse(args);
            CliRunner.Run(options);
        }
    }
}
