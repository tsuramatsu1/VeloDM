// Stands in for FFmpeg in the tests, for the one job VeloDM gives it: fetch two inputs and
// write them into one file. It prints the same progress lines and writes a small file that
// lists the inputs it was given.
//   an input containing "broken" -> fails the way FFmpeg does when it cannot read an input
//   an input containing "slow"   -> pauses between progress lines so a test can interrupt it

var inputs = new List<string>();
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "-i") inputs.Add(args[i + 1]);
string output = args[^1];

if (inputs.Any(input => input.Contains("broken")))
{
    Console.Error.WriteLine("[https @ 000001] HTTP error 403 Forbidden");
    Console.Error.WriteLine(inputs.First(input => input.Contains("broken")) + ": Server returned 403 Forbidden (access denied)");
    return 1;
}

int delay = inputs.Any(input => input.Contains("slow")) ? 400 : 20;
foreach (int size in new[] { 500, 1500, 2600 })
{
    File.WriteAllBytes(output, new byte[size]);
    Console.WriteLine("bitrate=1200.0kbits/s");
    Console.WriteLine("total_size=" + size);
    Console.WriteLine("progress=continue");
    Thread.Sleep(delay);
}
File.WriteAllLines(output, args);
Console.WriteLine("progress=end");
return 0;
