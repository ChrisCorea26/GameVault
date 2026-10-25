using GameVault.Core.Cartridge.Mappers;

namespace GameVault.Core.Cartridge;

public static class MapperFactory
{
    public static IMapper Create(INesRom rom) => rom.Mapper switch
    {
        0 => new Nrom(rom),
        _ => throw new UnsupportedMapperException(rom.Mapper),
    };
}
