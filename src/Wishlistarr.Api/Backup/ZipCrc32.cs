namespace Wishlistarr.Api.Backup;

internal static class ZipCrc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint value = ~crc;
        foreach (byte item in data)
        {
            value = Table[(value ^ item) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
