using GameVault.Core.Cartridge.Mappers;

namespace GameVault.Core.Cartridge;

public static class MapperFactory
{
    public static IMapper Create(INesRom rom) => rom.Mapper switch
    {
        0 => new Nrom(rom),
        1 => new Mmc1(rom),
        2 => new UxRom(rom),
        3 => new CnRom(rom),
        4 or 118 or 119 => new Mmc3(rom),
        5 => new Mmc5(rom),
        7 => new AxRom(rom),
        9 or 10 => new Mmc2(rom),
        11 => new ColorDreams(rom),
        19 => new Namco163(rom),
        21 or 22 or 23 or 25 => new Vrc24(rom),
        24 or 26 => new Vrc6(rom),
        69 => new Fme7(rom),
        85 => new Vrc7(rom),
        34 => new Mapper34(rom),
        66 => new GxRom(rom),
        71 => new Camerica(rom),
        79 => new Nina0306(rom),
        87 => new Mapper87(rom),
        140 => new Mapper140(rom),
        206 => new Namco108(rom),
        _ => throw new UnsupportedMapperException(rom.Mapper),
    };
}
