using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gp;

static partial class TestMain
{
    /// <summary>A minimal x64 PE whose section table is exactly <paramref name="sections"/> (name, VirtualSize,
    /// Characteristics; each gets a 0x200-byte raw block), followed by <paramref name="tail"/>.</summary>
    static void WritePeWithSections(string path, IList<Tuple<string, uint, uint>> sections, byte[] tail = null)
    {
        var b = new byte[0x400 + 0x200 * sections.Count];
        W16(b, 0x00, 0x5A4D);
        W32(b, 0x3C, 0x80);
        int pe = 0x80;
        W32(b, pe, 0x00004550);
        W16(b, pe + 4, 0x8664);
        W16(b, pe + 6, (ushort)sections.Count);
        W16(b, pe + 20, 0xF0);
        W16(b, pe + 22, 0x22);
        int opt = pe + 24;
        W16(b, opt, 0x20B);
        W32(b, opt + 60, 0x400);
        int sec = opt + 0xF0;
        uint va = 0x1000;
        for (int i = 0; i < sections.Count; i++, sec += 40)
        {
            byte[] name = Encoding.ASCII.GetBytes(sections[i].Item1);
            Array.Copy(name, 0, b, sec, Math.Min(8, name.Length));
            W32(b, sec + 8, sections[i].Item2);
            W32(b, sec + 12, va);
            W32(b, sec + 16, 0x200);
            W32(b, sec + 20, (uint)(0x400 + 0x200 * i));
            W32(b, sec + 36, sections[i].Item3);
            va += (sections[i].Item2 + 0xFFF) & ~0xFFFu;
        }
        File.WriteAllBytes(path, tail == null ? b : b.Concat(tail).ToArray());
    }

    static Tuple<string, uint, uint> Sec(string name, uint virtualSize, uint characteristics)
    {
        return Tuple.Create(name, virtualSize, characteristics);
    }

    /// <summary>--protections &lt;folder&gt;: ProtectionScan.Detect for every exe below the folder, read-only.</summary>
    static int Protections(string dir)
    {
        if (!Directory.Exists(dir)) { Console.WriteLine("usage: _selftest.exe --protections <folder>"); return 2; }
        int exes = 0, flagged = 0;
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            string d = pending.Pop();
            try { foreach (var sub in Directory.GetDirectories(d)) pending.Push(sub); } catch { }
            string[] files;
            try { files = Directory.GetFiles(d, "*.exe"); } catch { continue; }
            foreach (var exe in files)
            {
                exes++;
                var found = ProtectionScan.Detect(exe);
                if (found.Count == 0) continue;
                flagged++;
                Console.WriteLine("  " + exe + "  ->  " + string.Join(", ", found.ToArray()));
            }
        }
        Console.WriteLine(exes + " exe(s) scanned, " + flagged + " with a protection warning.");
        return 0;
    }

    /// <summary>Denuvo / unknown-protection pre-flight (feature A).</summary>
    static void SelfTestProtection()
    {
        Console.WriteLine("\n[protection pre-flight]");
        const uint Code = 0x60000020, Data = 0x40000040;
        const uint Big = 0x180000;   // 1.5 MiB
        string work = Path.Combine(Path.GetTempPath(), "gp_selftest_prot_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            Func<string, IList<Tuple<string, uint, uint>>, byte[], List<string>> scan = (name, secs, tail) =>
            {
                string p = Path.Combine(work, name + ".exe");
                WritePeWithSections(p, secs, tail);
                return ProtectionScan.Detect(p);
            };
            Func<List<string>, string> show = l => l.Count == 0 ? "(nothing)" : string.Join(", ", l.ToArray());

            var stock = scan("stock", new[] { Sec(".text", Big, Code), Sec(".rdata", Big, Data), Sec(".data", 0x1000, 0xC0000040) },
                Encoding.ASCII.GetBytes("\0SteamAPI_ISteamUser_GetDenuvoTicketLocation\0"));
            Check(stock.Count == 0, "stock layout that names GetDenuvoTicketLocation reports nothing", show(stock));

            var rex = scan("rex", new[] { Sec(".text", 0x4000, Code), Sec(".rex", Big, Code) }, null);
            Check(rex.Count == 1 && rex[0] == "unrecognised protection (section '.rex')",
                  "a 1 MiB+ executable section under an unknown name is an unrecognised protection", show(rex));

            var rawOnly = scan("raw", new[] { Sec(".text", 0x4000, Code), Sec(".rex", 0x1000, Code) }, new byte[0x100000]);
            Check(rawOnly.Count == 0, "a small unknown executable section is not flagged", show(rawOnly));

            var denuvo = scan("denuvo", new[] { Sec(".text", 0x4000, Code), Sec(".xcode", Big, 0x20000000) }, Encoding.ASCII.GetBytes("..DeNuVo.."));
            Check(denuvo.Count == 1 && denuvo[0] == "Denuvo", "the same section plus the string \"denuvo\" is Denuvo (execute-only flag counts)", show(denuvo));

            var wide = scan("wide", new[] { Sec(".text", 0x4000, Code), Sec(".arch", Big, 0x20) }, Encoding.Unicode.GetBytes("Powered by DENUVO"));
            Check(wide.Count == 1 && wide[0] == "Denuvo", "UTF-16 \"DENUVO\" counts too (code-only flag counts)", show(wide));

            var stockNames = scan("stocknames", new[] { Sec(".text", Big, Code), Sec("il2cpp", Big, Code), Sec(".textbss", Big, Code),
                Sec("UPX0", Big, Code), Sec("UPX1", Big, Code), Sec("IPPCODE", Big, Code), Sec(".bind", Big, Code) }, Encoding.ASCII.GetBytes("denuvo"));
            Check(stockNames.Count == 0, "big .text/il2cpp/.textbss/UPX/IPPCODE/.bind sections are normal, even with \"denuvo\" in the file", show(stockNames));

            var data = scan("data", new[] { Sec(".text", 0x4000, Code), Sec(".pak", Big, Data) }, null);
            Check(data.Count == 0, "a big non-executable section is not flagged", show(data));

            var themida = scan("themida", new[] { Sec("", Big, Code), Sec(".themida", Big, Code), Sec(".boot", Big, Code) }, null);
            Check(themida.Count == 1 && themida[0] == "Themida", "Themida's own sections are not reported a second time as unrecognised", show(themida));

            var vmp = scan("vmp", new[] { Sec(".text", 0x4000, Code), Sec(".vmp0", Big, Code) }, null);
            Check(vmp.Count == 1 && vmp[0] == "VMProtect", "VMProtect is still recognised by name", show(vmp));

            var both = scan("both", new[] { Sec(".text", 0x4000, Code), Sec(".vmp0", Big, Code), Sec(".xtext", Big, Code) }, Encoding.ASCII.GetBytes("denuvo"));
            Check(both.SequenceEqual(new[] { "VMProtect", "Denuvo" }), "a named protector and Denuvo are both reported", show(both));

            // the string search carries the chunk tail over, so a match straddling the 1 MB read boundary is found
            string straddle = Path.Combine(work, "straddle.bin");
            var buf = new byte[(1 << 20) + 64];
            Encoding.Unicode.GetBytes("denuvo").CopyTo(buf, (1 << 20) - 5);
            File.WriteAllBytes(straddle, buf);
            Check(ProtectionScan.ContainsDenuvo(straddle), "the denuvo search finds a match across the 1 MB read boundary", null);
            File.WriteAllBytes(straddle, new byte[(1 << 20) + 64]);
            Check(!ProtectionScan.ContainsDenuvo(straddle) && !ProtectionScan.ContainsDenuvo(Path.Combine(work, "missing.exe")),
                  "no match, and a missing file, report no Denuvo", null);
            Check(ProtectionScan.Detect(Path.Combine(work, "missing.exe")).Count == 0, "a missing exe reports nothing", null);
        }
        catch (Exception ex) { Check(false, "protection tests ran without throwing", ex.ToString()); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
