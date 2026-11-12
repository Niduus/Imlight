using Imcodec.TypeDump;
var (m, _) = TypeDumper.Dump(args[0], false, Console.Error);
using var f = File.Create(args[1]);
TypeDumper.WriteJson(m, f);
