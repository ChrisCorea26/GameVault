using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>The SPC7110 (snes-chips-3.md §2).</summary>
public sealed class Spc7110Tests
{
    /// <summary>
    /// Decoder output from ares' own SPC7110 decompressor (ares/sfc/coprocessor/spc7110/decompressor.cpp, ISC licence),
    /// compiled as it is and run over the same data: 64 rows for each mode and starting address. The data is the
    /// generator in <see cref="DataRom"/>.
    /// </summary>
    private static readonly Dictionary<(int Mode, int Origin), uint[]> AresVectors = new()
    {
        [(0, 0x0)] =
        [
            204u, 52252u, 13376696u, 3424434384u, 481874035u, 3100668854u, 3497244345u, 1941354872u,
            3065608306u, 3111678594u, 2020770492u, 1921170504u, 2193377397u, 3158865281u, 1215660307u, 1971393368u,
            2165528799u, 324591518u, 1491050010u, 3751680600u, 2652526800u, 442028188u, 1490066570u, 3499920103u,
            2626348876u, 2330414217u, 3880552842u, 1284082400u, 2307580148u, 2329998361u, 3774093742u, 4095323877u,
            430892392u, 2934269996u, 3848809486u, 1747717696u, 739131604u, 239129642u, 1087646233u, 3559528768u,
            706297959u, 423651134u, 1080508077u, 1732160832u, 1051541607u, 2906679084u, 1080503309u, 1730940224u,
            739065972u, 222327932u, 1081375934u, 1954332329u, 2092870082u, 3198796511u, 2848120760u, 3269441706u,
            3753421508u, 3098199252u, 2865026066u, 3302232765u, 3557998040u, 314431680u, 3185098766u, 3636465343u,
        ],
        [(0, 0x1234)] =
        [
            135u, 34705u, 8884678u, 2274477776u, 2445725783u, 3335542583u, 3495376836u, 1463272612u,
            935634006u, 3299104309u, 2757113281u, 1446363554u, 901882452u, 3248641120u, 2723438769u, 1415623074u,
            1622254308u, 2980242528u, 2732875782u, 3831498369u, 1611039207u, 109176675u, 2179425024u, 3882025092u,
            1660978371u, 8700757u, 2227393826u, 3277136568u, 1428338869u, 582530393u, 3098892662u, 3042539208u,
            1500956903u, 1992877967u, 3370618746u, 3884939854u, 2407157482u, 2051992239u, 1324003127u, 3937351661u,
            2939678124u, 938323004u, 3987487801u, 2889628053u, 1010406826u, 966109764u, 2510963771u, 2856598486u,
            1144772266u, 1003924039u, 3601483581u, 2856795546u, 1195219620u, 1033544712u, 2594441269u, 2752001434u,
            137730743u, 899331851u, 2595687206u, 3070961296u, 187076791u, 647018359u, 2427942742u, 3078051558u,
        ],
        [(0, 0x8000)] =
        [
            189u, 48474u, 12409571u, 3176850422u, 1524889278u, 3824598760u, 4139706483u, 3202905077u,
            3899913625u, 1945475407u, 4120465267u, 2572120994u, 1332978380u, 1940048927u, 2731286278u, 3424585426u,
            520540761u, 114448767u, 3529080740u, 1501537330u, 2141467192u, 2754754687u, 842563445u, 947877330u,
            2138428143u, 1976758142u, 3538910960u, 4018073828u, 2129716287u, 4041490248u, 3829352610u, 1061724677u,
            1218577897u, 2718296527u, 99209125u, 3922699728u, 3483750410u, 2781874845u, 3490356504u, 178067568u,
            2635624663u, 410048490u, 1893198442u, 3622464068u, 3932832960u, 1782890747u, 1153497962u, 3237702369u,
            4218085756u, 1793162366u, 3783032547u, 2088690547u, 2128835401u, 3815983464u, 1934190838u, 1231615687u,
            1761003355u, 4140260200u, 3344656608u, 1533599783u, 1759520701u, 3760700808u, 666732544u, 3179806741u,
        ],
        [(1, 0x0)] =
        [
            59385u, 19302u, 8631u, 12985u, 52700u, 25528u, 15062u, 53086u,
            20321u, 8992u, 38642u, 19419u, 17480u, 62148u, 31604u, 36026u,
            54856u, 31028u, 15387u, 19575u, 36286u, 52184u, 40172u, 57979u,
            26897u, 39590u, 29487u, 12568u, 39054u, 58479u, 24934u, 10265u,
            57883u, 6791u, 14493u, 30406u, 27876u, 15577u, 30400u, 39123u,
            3149u, 7244u, 23043u, 12187u, 38601u, 19047u, 1443u, 40525u,
            36579u, 18140u, 9198u, 12599u, 50179u, 48834u, 50681u, 7912u,
            60924u, 33754u, 50151u, 51102u, 49997u, 50088u, 24851u, 36878u,
        ],
        [(1, 0x1234)] =
        [
            1760u, 4889u, 14385u, 58828u, 47139u, 52893u, 25667u, 50996u,
            15244u, 1398u, 50938u, 36165u, 48283u, 23608u, 40030u, 65478u,
            50765u, 12675u, 46204u, 20163u, 41020u, 37334u, 58990u, 4578u,
            57717u, 59686u, 7803u, 14722u, 52834u, 23711u, 13635u, 35205u,
            62112u, 41287u, 13351u, 37663u, 19760u, 42554u, 47045u, 8422u,
            40891u, 4608u, 26574u, 39676u, 52531u, 29472u, 36285u, 13987u,
            3534u, 11291u, 65420u, 47047u, 28198u, 35182u, 46798u, 26243u,
            61542u, 52225u, 34881u, 64479u, 58686u, 56549u, 60578u, 13001u,
        ],
        [(1, 0x8000)] =
        [
            26241u, 55088u, 5691u, 13658u, 47067u, 14235u, 14106u, 30363u,
            13467u, 38427u, 46623u, 37427u, 38455u, 33313u, 6919u, 23149u,
            23405u, 19245u, 23087u, 23837u, 56525u, 22729u, 55881u, 63229u,
            42127u, 46783u, 38816u, 54450u, 65210u, 60194u, 48648u, 63472u,
            41749u, 2570u, 60149u, 61343u, 59505u, 16727u, 6139u, 16082u,
            44200u, 21250u, 65272u, 37421u, 11728u, 3397u, 59645u, 170u,
            65535u, 10905u, 174u, 24102u, 3212u, 29051u, 34114u, 1525u,
            14911u, 38325u, 299u, 11002u, 28913u, 15687u, 19008u, 43551u,
        ],
        [(2, 0x0)] =
        [
            121496063u, 1967353514u, 2473736579u, 1514501350u, 1136748180u, 2515098513u, 3914162332u, 851685306u,
            4103120414u, 2516342373u, 2958608797u, 1943692931u, 3332399571u, 2280901985u, 3834729430u, 529412935u,
            3496626489u, 1436403260u, 3634488428u, 1533746680u, 1887537208u, 2026551774u, 1703383858u, 1791176283u,
            1468501017u, 2355744349u, 522543948u, 1643133237u, 904350904u, 3557979671u, 500871276u, 3703656264u,
            1001874909u, 2953722211u, 2794625075u, 3245974925u, 2680743499u, 1370589450u, 904313572u, 3627715782u,
            2341779410u, 1726616124u, 728889452u, 3402771440u, 1474556121u, 701173603u, 478925926u, 2787908911u,
            1466593673u, 2444850614u, 1712870700u, 1726469338u, 2770024335u, 3130545226u, 2220541743u, 4176055200u,
            3542834878u, 3280799138u, 262568371u, 3381101480u, 261323989u, 1682590151u, 2641213089u, 472524955u,
        ],
        [(2, 0x1234)] =
        [
            1426064579u, 422589068u, 1938066468u, 2153969732u, 3850944214u, 4064579794u, 3867881488u, 3452117367u,
            3677454949u, 2041407608u, 537597520u, 2050310828u, 3083938737u, 1943857691u, 1102288053u, 749250292u,
            3576275823u, 800532589u, 1238967248u, 3725957426u, 1491654078u, 2110788893u, 1513019133u, 1395561481u,
            2699584966u, 4037993484u, 3648681371u, 2291213094u, 2116716145u, 2249534730u, 2460225704u, 959898970u,
            1528590513u, 1892217518u, 3702528073u, 746505653u, 2705704523u, 3405155375u, 2336425576u, 2832478390u,
            3156569485u, 1794658219u, 1379874894u, 3260283099u, 3025565104u, 232567273u, 2460743356u, 2850705195u,
            834099905u, 1664841084u, 161470475u, 1416413081u, 1033840438u, 56048224u, 2691024624u, 3633428796u,
            511477045u, 123691277u, 2150940390u, 3388739481u, 809403367u, 1885754648u, 2396397523u, 158291342u,
        ],
        [(2, 0x8000)] =
        [
            3016032415u, 836859651u, 3615824165u, 2545113461u, 3197787097u, 2528531156u, 2610952944u, 2583689402u,
            888035603u, 1848021313u, 3686249061u, 824780877u, 3607793152u, 3251264578u, 2166049042u, 1102257010u,
            3644037445u, 3037772844u, 331336057u, 3196897827u, 3016158242u, 2640375300u, 1186686188u, 811758551u,
            368298576u, 2885469431u, 392262906u, 1893848726u, 1829405468u, 608088939u, 1618355746u, 3986881379u,
            4175856249u, 732126147u, 1741311949u, 1876429295u, 457758384u, 101539119u, 1346177882u, 3564621835u,
            1577634125u, 2528655527u, 2003932947u, 106635065u, 1434520300u, 489641038u, 1011109656u, 1778043327u,
            253623528u, 75359299u, 3860052189u, 639344902u, 2195695980u, 1174774734u, 250891634u, 3733750662u,
            3142345710u, 4007656772u, 472902502u, 1761025518u, 3708605760u, 1935825995u, 2126925274u, 541408141u,
        ],
    };

    /// <summary>64 KB of data ROM from a fixed generator (the same one the reference program uses).</summary>
    private static byte[] DataRom()
    {
        var data = new byte[0x10000];
        var seed = 12345u;
        for (var i = 0; i < data.Length; i++)
        {
            seed = seed * 1103515245u + 12345u;
            data[i] = (byte)(seed >> 16);
        }

        return data;
    }

    /// <summary>A cartridge image: 1 MB of program ROM (a HiROM header naming the SPC7110), then the data ROM.</summary>
    private static byte[] Image(byte[]? dataRom = null, byte chip = 0xF5)
    {
        var program = SnesTests.Rom([0x80, 0xFE], hiRom: true, size: 0x100000, chip: chip, ramSize: 3);
        program[0xFFC0 + 0x15] = 0x3A; // HiROM with the SPC7110
        program[0xFFC0 + 0x1A] = 0x33; // an extended header, subtype 0
        program[0xFFBF] = 0x00;
        return [.. program, .. dataRom ?? DataRom()];
    }

    private static Spc7110 Chip(byte[]? dataRom = null, byte chip = 0xF5)
    {
        var cartridge = new SnesCartridge(Image(dataRom, chip));
        var spc7110 = cartridge.Coprocessor.Should().BeOfType<Spc7110>().Subject;
        spc7110.Power();
        return spc7110;
    }

    [Theory]
    [InlineData(0, 0x0)]
    [InlineData(0, 0x1234)]
    [InlineData(0, 0x8000)]
    [InlineData(1, 0x0)]
    [InlineData(1, 0x1234)]
    [InlineData(1, 0x8000)]
    [InlineData(2, 0x0)]
    [InlineData(2, 0x1234)]
    [InlineData(2, 0x8000)]
    public void The_decompressor_matches_ares_row_for_row(int mode, int origin)
    {
        var decompressor = new Spc7110.Decompressor(Chip());

        decompressor.Initialize(mode, origin);
        var rows = new uint[64];
        for (var i = 0; i < rows.Length; i++)
        {
            decompressor.Decode();
            rows[i] = decompressor.Result;
        }

        rows.Should().Equal(AresVectors[(mode, origin)]);
    }

    [Fact]
    public void The_cpu_reads_whole_tiles_through_4800()
    {
        // A table at $F000 whose entry 2 says: mode 1 (2 bits a pixel), data at $001234.
        var data = DataRom();
        data[0xF008] = 1;
        data[0xF009] = 0x00;
        data[0xF00A] = 0x12;
        data[0xF00B] = 0x34;
        var chip = Chip(data);

        chip.Write(0x004801, 0x00);
        chip.Write(0x004802, 0xF0);
        chip.Write(0x004803, 0x00);
        chip.Write(0x004804, 2);
        chip.Write(0x004805, 0);
        chip.Write(0x004806, 0); // start
        chip.Read(0x00480C, 0).Should().Be(0x80, "the data is ready");

        // A 2 bpp tile is 16 bytes: each row's two bitplanes, from one decoded row after another.
        var rows = AresVectors[(1, 0x1234)];
        var tiles = Enumerable.Range(0, 32).Select(i => i < 16 ? chip.Read(0x004800, 0) : chip.Read(0x500000, 0)).ToArray();
        for (var row = 0; row < 16; row++)
        {
            tiles[row * 2].Should().Be((byte)rows[row], $"row {row}'s first plane");
            tiles[row * 2 + 1].Should().Be((byte)(rows[row] >> 8));
        }
    }

    [Fact]
    public void The_data_port_reads_from_its_offset_and_steps_as_set()
    {
        var data = DataRom();
        var chip = Chip(data);

        chip.Write(0x004811, 0x00);
        chip.Write(0x004812, 0x10);
        chip.Write(0x004813, 0x00); // offset $1000
        chip.Read(0x004810, 0).Should().Be(data[0x1000]);
        chip.Read(0x004810, 0).Should().Be(data[0x1001], "each read steps by one");

        chip.Write(0x004816, 0x10);
        chip.Write(0x004817, 0x00); // stride $10
        chip.Write(0x004818, 0x01); // step by the stride
        chip.Read(0x004810, 0).Should().Be(data[0x1002]);
        chip.Read(0x004810, 0).Should().Be(data[0x1012]);

        chip.Write(0x004818, 0x0A); // a signed adjust, added to reads
        chip.Write(0x004814, 0xFE);
        chip.Write(0x004815, 0xFF); // -2
        chip.Read(0x004810, 0).Should().Be(data[0x1022 - 2]);

        chip.Write(0x004818, 0x20 | 0x08); // writing $4814 adds the adjust to the offset
        chip.Write(0x004815, 0x00);
        chip.Write(0x004814, 0x10);
        chip.Read(0x004813, 0).Should().Be(0);
        (chip.Read(0x004811, 0) | chip.Read(0x004812, 0) << 8).Should().Be(0x1033, "$1023 + $10");
        chip.Read(0x004810, 0).Should().Be(data[0x1033]);
    }

    [Theory]
    [InlineData(false, 300, 200, 60000u)]
    [InlineData(true, -300, 200, 0xFFFF15A0u)]
    public void The_alu_multiplies(bool signed, int a, int b, uint product)
    {
        var chip = Chip();
        chip.Write(0x00482E, (byte)(signed ? 1 : 0));
        chip.Write(0x004820, (byte)a);
        chip.Write(0x004821, (byte)(a >> 8));
        chip.Write(0x004824, (byte)b);
        chip.Write(0x004825, (byte)(b >> 8));

        (chip.Read(0x00482F, 0) & 0x80).Should().Be(0, "done");
        var result = (uint)(chip.Read(0x004828, 0) | chip.Read(0x004829, 0) << 8 | chip.Read(0x00482A, 0) << 16 | chip.Read(0x00482B, 0) << 24);
        result.Should().Be(product);
    }

    [Theory]
    [InlineData(false, 100000u, 7, 14285u, 5)]
    [InlineData(true, 0xFFFE7960u, 7, 0xFFFFC833u, 0xFFFB)] // -100000 / 7 = -14285 remainder -5
    [InlineData(false, 1234u, 0, 0u, 1234)]                  // ÷ 0: 0, and the dividend as the remainder
    public void The_alu_divides(bool signed, uint dividend, int divisor, uint quotient, int remainder)
    {
        var chip = Chip();
        chip.Write(0x00482E, (byte)(signed ? 1 : 0));
        for (var i = 0; i < 4; i++)
        {
            chip.Write(0x004820 + (uint)i, (byte)(dividend >> (8 * i)));
        }

        chip.Write(0x004826, (byte)divisor);
        chip.Write(0x004827, (byte)(divisor >> 8));

        var q = (uint)(chip.Read(0x004828, 0) | chip.Read(0x004829, 0) << 8 | chip.Read(0x00482A, 0) << 16 | chip.Read(0x00482B, 0) << 24);
        q.Should().Be(quotient);
        (chip.Read(0x00482C, 0) | chip.Read(0x00482D, 0) << 8).Should().Be(remainder);
    }

    [Fact]
    public void The_rom_windows_show_the_program_rom_and_the_chosen_data_rom_megabytes()
    {
        var data = new byte[0x300000]; // 3 MB of data ROM: each megabyte marked
        data[0x000005] = 0xA0;
        data[0x100005] = 0xA1;
        data[0x200005] = 0xA2;
        var cartridge = new SnesCartridge(Image(data));
        var chip = (Spc7110)cartridge.Coprocessor!;
        chip.Power();
        chip.Write(0x004834, 2); // a 32 Mbit data ROM

        chip.Read(0xC0FFD5, 0).Should().Be(0x3A, "$C0–$CF is the program ROM");
        chip.Read(0x00FFD5, 0).Should().Be(0x3A, "and so is $00:8000–$FFFF");
        chip.Read(0xD00005, 0).Should().Be(0xA0, "$D0 shows data ROM megabyte 0 after power-on");
        chip.Read(0xE00005, 0).Should().Be(0xA1, "$E0 megabyte 1");
        chip.Read(0xF00005, 0).Should().Be(0xA2, "$F0 megabyte 2");

        chip.Write(0x004831, 2);
        chip.Read(0xD00005, 0).Should().Be(0xA2);
        chip.Read(0x908005, 0).Should().Be(chip.Read(0xD08005, 0), "$90–$9F is the LoROM mirror of $D0–$DF");
        Spc7110.Mirror(0x300000, 0x300000).Should().Be(0x200000, "a 3 MB ROM repeats its last megabyte");
    }

    [Fact]
    public void Sram_answers_only_while_enabled()
    {
        var cartridge = new SnesCartridge(Image());
        var chip = (Spc7110)cartridge.Coprocessor!;
        chip.Power();
        cartridge.HasBattery.Should().BeTrue();

        chip.Write(0x006010, 0x5A);
        chip.Read(0x006010, 0xFF).Should().Be(0, "SRAM is off after power-on");

        chip.Write(0x004830, 0x80);
        chip.Write(0x006010, 0x5A);
        chip.Read(0x306010, 0).Should().Be(0x5A, "every bank shows the same 8 KB");
        cartridge.BatteryRam[0x10].Should().Be(0x5A);
    }

    [Fact]
    public void A_save_state_carries_the_decompressor_mid_tile()
    {
        var data = DataRom();
        data[0] = 2; // mode 2 from $008000
        data[1] = 0x00;
        data[2] = 0x80;
        data[3] = 0x00;
        var chip = Chip(data);
        chip.Write(0x004804, 0);
        chip.Write(0x004806, 0);
        for (var i = 0; i < 20; i++)
        {
            chip.Read(0x004800, 0);
        }

        var writer = new StateWriter();
        chip.SaveState(writer);
        var next = Enumerable.Range(0, 40).Select(_ => chip.Read(0x004800, 0)).ToArray();

        chip.LoadState(new StateReader(writer.ToArray()));
        Enumerable.Range(0, 40).Select(_ => chip.Read(0x004800, 0)).Should().Equal(next);
    }
}
