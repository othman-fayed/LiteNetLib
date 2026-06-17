using System;
using System.Text;

namespace LibSample
{
    public static class Program
    {
        private static readonly IExample[] Examples = {
            new EchoMessagesTest(),
            new HolePunchServerTest(),
            new BroadcastTest(),
            new SerializerBenchmark(),
            new SpeedBench(),
            new PacketProcessorExample(),
            new AesEncryptionTest(),
            new NtpTest(),
        };

        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0].Equals("natpunch", StringComparison.OrdinalIgnoreCase))
            {
                if (args[1].Equals("server", StringComparison.OrdinalIgnoreCase))
                {
                    NatPunchStandaloneTest.RunServer();
                    return;
                }
                if (args[1].Equals("client", StringComparison.OrdinalIgnoreCase))
                {
                    NatPunchStandaloneTest.RunClient(args.Length >= 3 ? args[2] : null);
                    return;
                }
                if (args[1].Equals("localtest", StringComparison.OrdinalIgnoreCase))
                {
                    NatPunchStandaloneTest.RunLocalTest();
                    return;
                }
                Console.WriteLine("Usage:");
                Console.WriteLine("  natpunch server");
                Console.WriteLine("  natpunch client [relay-server-host]  (default: 37.34.188.126)");
                Console.WriteLine("  natpunch localtest    (run server+2 clients in one process to see all logs)");
                return;
            }

            AppendExampleMenu(MenuStringBuilder);
            WriteAndClean(MenuStringBuilder);

            do
            {
                Console.Write("Write command: ");
                var input = Console.ReadLine();

                if (input != null)
                {
                    var lcInput = input.ToLower();

                    if (lcInput == "help" || lcInput == "h")
                    {
                        AppendFullHelpMenu(MenuStringBuilder);
                        WriteAndClean(MenuStringBuilder);
                        continue;
                    }

                    if (lcInput == "quit" || lcInput == "exit" || lcInput == "q" || lcInput == "e")
                    {
                        break;
                    }

                    if (int.TryParse(input, out var optionKey))
                    {
                        if (optionKey < 0 || optionKey >= Examples.Length)
                        {
                            PrintInvalidCommand(input);
                            continue;
                        }

                        ((IExample)Activator.CreateInstance(Examples[optionKey].GetType())).Run();
                    }
                    else
                    {
                        PrintInvalidCommand(input);
                    }
                }
                else
                {
                    PrintInvalidCommand(string.Empty);
                }
            } while (true);
        }

        private static void PrintInvalidCommand(string invalidInput)
        {
            AppendInvalidCommand(MenuStringBuilder, invalidInput);
            WriteAndClean(MenuStringBuilder);
        }

        private static readonly StringBuilder MenuStringBuilder = new StringBuilder();

        private static void WriteAndClean(StringBuilder sb)
        {
            Console.WriteLine(sb.ToString());
            sb.Clear();
        }

        private static void AppendInvalidCommand(StringBuilder sb, string invalidInput)
        {
            sb.Append("Invalid input \"");
            sb.Append(string.IsNullOrWhiteSpace(invalidInput) ? "[Whitespace/Empty Line]" : invalidInput);
            sb.AppendLine("\" command. Write \"help\" command for more information.");
        }

        private static void AppendFullHelpMenu(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("\"help/h\" - write helper text for this console menu.");
            sb.AppendLine("\"exit/e/quit/q\" - close app");
            AppendExampleMenu(sb);
            sb.AppendLine();
        }

        private static void AppendExampleMenu(StringBuilder sb)
        {
            for (var i = 0; i < Examples.Length; i++)
            {
                var example = Examples[i];
                sb.AppendLine($"\"{i}\" - Example of {example.GetType().Name}");
            }
        }
    }
}
