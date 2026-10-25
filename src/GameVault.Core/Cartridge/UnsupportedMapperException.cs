namespace GameVault.Core.Cartridge;

public sealed class UnsupportedMapperException(int mapper)
    : Exception($"Mapper {mapper} is not supported yet.")
{
    public int Mapper { get; } = mapper;
}
