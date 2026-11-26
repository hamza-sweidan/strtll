using System.Text;
using Nse.Cli;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return Cli.Run(args, Console.Out, Console.Error);
