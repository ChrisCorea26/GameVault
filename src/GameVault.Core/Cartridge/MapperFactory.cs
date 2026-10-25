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
        4 => new Mmc3(rom),
        7 => new AxRom(rom),
        _ => throw new UnsupportedMapperException(rom.Mapper),
    };
}
