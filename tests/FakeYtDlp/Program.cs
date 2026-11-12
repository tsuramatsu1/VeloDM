// Stands in for yt-dlp in the tests: it prints the same kind of lines for the options
// StreamTask passes, and writes a small file where the real program would put the video.
//   address contains "unsupported" -> fails the way yt-dlp does for a page it cannot read
//   address contains "broken"      -> fails with another error
//   address contains "slow"        -> pauses between progress lines so a test can interrupt it

string address = args[^1];
if (args.Contains("--skip-download"))
{
    // Asked what the video is, not to download it.
    if (address.Contains("unsupported"))
    {
        Console.Error.WriteLine("ERROR: Unsupported URL: " + address);
        return 1;
    }
    Console.WriteLine("Fake: Title?");
    Console.WriteLine(address.Contains("live") ? "NA" : "0:19");
    return 0;
}
string template = args[Array.IndexOf(args, "-o") + 1];
string output = template.Replace("%(title).150B", "Fake Title").Replace("%(ext)s", "mp4").Replace("%%", "%");
File.WriteAllLines(output + ".args.txt", args);

if (address.Contains("unsupported"))
{
    Console.Error.WriteLine("ERROR: Unsupported URL: " + address);
    return 1;
}
if (address.Contains("broken"))
{
    Console.Error.WriteLine("WARNING: something minor");
    Console.Error.WriteLine("ERROR: [generic] This video is unavailable");
    return 1;
}

int delay = address.Contains("slow") ? 400 : 20;
Console.WriteLine("VDMTITLE|Fake Title");
Console.WriteLine("VDMSIZE|3000");
foreach (var (file, total) in new[] { ("video.f1.mp4", 2000), ("video.f2.m4a", 1000) })
{
    for (int done = total / 4; done < total; done += total / 4)
    {
        Console.WriteLine($"VDM|downloading|{done}|{total}|NA|1500.5|3|{file}");
        Thread.Sleep(delay);
    }
    Console.WriteLine($"VDM|finished|{total}|{total}|NA|NA|NA|{file}");
}
File.WriteAllBytes(output, new byte[3000]);
Console.WriteLine("VDMFILE|" + output);
return 0;
