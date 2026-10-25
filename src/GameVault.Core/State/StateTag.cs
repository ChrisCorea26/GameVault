using System.Text;

namespace GameVault.Core.State;

/// <summary>A four-character ASCII chunk identifier, such as <c>"CPU "</c>.</summary>
public readonly record struct StateTag(uint Value)
{
    public static StateTag From(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4 || !Ascii.IsValid(tag))
        {
            throw new ArgumentException("State tags must be exactly four ASCII characters.", nameof(tag));
        }

        return new StateTag((uint)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24));
    }

    public override string ToString() =>
        string.Create(4, Value, static (span, v) =>
        {
            for (var i = 0; i < 4; i++)
            {
                span[i] = (char)((v >> (8 * i)) & 0xFF);
            }
        });
}
