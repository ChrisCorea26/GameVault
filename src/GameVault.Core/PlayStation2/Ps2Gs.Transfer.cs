using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// Transfers (ps2tek: "GS Transfers"): host to local through HWREG (the GIF's IMAGE mode), local to host read back
/// through the GIF FIFO while BUSDIR is reversed, and local to local copies. Pixels are packed in the stream at the
/// format's size (32, 24, 16, 8 or 4 bits, low nibble first) and walk the TRXREG rectangle from TRXPOS's corner,
/// wrapping at 2048. Ported in part from Play! (BSD-2-Clause): Source/gs/GSHandler.cpp (TransferWriteHandler*).
/// </summary>
public sealed partial class Ps2Gs
{
    // Host to local.
    private bool _upActive;
    private int _upBp;
    private int _upBw;
    private int _upPsm;
    private int _upX0;
    private int _upY0;
    private int _upWidth;
    private int _upX;
    private int _upY;
    private long _upPixelsLeft;
    private int _upBits;
    private ulong _upAcc;
    private int _upAccBits;

    // Local to host.
    private bool _downActive;
    private int _downBp;
    private int _downBw;
    private int _downPsm;
    private int _downX0;
    private int _downY0;
    private int _downWidth;
    private int _downX;
    private int _downY;
    private long _downPixelsLeft;
    private long _downQwordsLeft;
    private int _downBits;
    private uint _downCarry;
    private int _downCarryBits;

    /// <summary>A host-to-local transfer is waiting for HWREG data.</summary>
    public bool UploadActive => _upActive;

    /// <summary>Quadwords of a local-to-host transfer still to be read.</summary>
    public long DownloadQwordsLeft => _downActive ? _downQwordsLeft : 0;

    private void ResetTransfer()
    {
        _upActive = false;
        _upAcc = 0;
        _upAccBits = 0;
        _downActive = false;
        _downQwordsLeft = 0;
    }

    private void BeginTransfer()
    {
        var bitblt = _regs[RegBitBltBuf];
        var pos = _regs[RegTrxPos];
        var reg = _regs[RegTrxReg];
        var width = (int)(reg & 0xFFF);
        var height = (int)((reg >> 32) & 0xFFF);
        var sbp = (int)(bitblt & 0x3FFF);
        var sbw = (int)((bitblt >> 16) & 0x3F);
        var spsm = (int)((bitblt >> 24) & 0x3F);
        var dbp = (int)((bitblt >> 32) & 0x3FFF);
        var dbw = (int)((bitblt >> 48) & 0x3F);
        var dpsm = (int)((bitblt >> 56) & 0x3F);
        var ssax = (int)(pos & 0x7FF);
        var ssay = (int)((pos >> 16) & 0x7FF);
        var dsax = (int)((pos >> 32) & 0x7FF);
        var dsay = (int)((pos >> 48) & 0x7FF);

        switch (_regs[RegTrxDir] & 3)
        {
            case 0:
                _upActive = width > 0 && height > 0;
                _upBp = dbp;
                _upBw = dbw;
                _upPsm = dpsm;
                _upX0 = dsax;
                _upY0 = dsay;
                _upWidth = width;
                _upX = _upY = 0;
                _upPixelsLeft = (long)width * height;
                _upBits = BitsPerPixel(dpsm);
                _upAcc = 0;
                _upAccBits = 0;
                break;
            case 1:
                _downActive = width > 0 && height > 0;
                _downBp = sbp;
                _downBw = sbw;
                _downPsm = spsm;
                _downX0 = ssax;
                _downY0 = ssay;
                _downWidth = width;
                _downX = _downY = 0;
                _downPixelsLeft = (long)width * height;
                _downBits = BitsPerPixel(spsm);
                _downQwordsLeft = ((_downPixelsLeft * _downBits) + 127) / 128;
                _downCarry = 0;
                _downCarryBits = 0;
                break;
            case 2:
                CopyLocal(sbp, sbw, spsm, ssax, ssay, dbp, dbw, dpsm, dsax, dsay, width, height, (int)((pos >> 59) & 3));
                break;
        }
    }

    /// <summary>HWREG: 64 bits of image data for the active host-to-local transfer (ignored when there is none).</summary>
    public void WriteHwReg(ulong value)
    {
        if (!_upActive)
        {
            return;
        }

        if (_upBits == 32)
        {
            UploadPixel((uint)value);
            if (_upActive)
            {
                UploadPixel((uint)(value >> 32));
            }

            return;
        }

        for (var i = 0; i < 8 && _upActive; i++)
        {
            _upAcc |= ((value >> (i * 8)) & 0xFF) << _upAccBits;
            _upAccBits += 8;
            while (_upAccBits >= _upBits && _upActive)
            {
                UploadPixel((uint)(_upAcc & ((1UL << _upBits) - 1)));
                _upAcc >>= _upBits;
                _upAccBits -= _upBits;
            }
        }
    }

    private void UploadPixel(uint pixel)
    {
        WritePixel(_upPsm, _upBp, _upBw, _upX0 + _upX, _upY0 + _upY, pixel);
        if (++_upX == _upWidth)
        {
            _upX = 0;
            _upY++;
        }

        if (--_upPixelsLeft == 0)
        {
            _upActive = false;
            _upAcc = 0;
            _upAccBits = 0;
        }
    }

    /// <summary>
    /// Local to host: fills <paramref name="destination"/> with the next quadwords of the active transfer and returns
    /// how many. Data is produced only while BUSDIR points from the GS to the host.
    /// </summary>
    public int ReadDownload(Span<UInt128> destination)
    {
        if (!_downActive || (_busdir & 1) == 0)
        {
            return 0;
        }

        var count = 0;
        while (count < destination.Length && _downQwordsLeft > 0)
        {
            // A 24-bit pixel can straddle two quadwords; its upper bits wait in the carry.
            var acc = (UInt128)_downCarry;
            var bits = _downCarryBits;
            _downCarry = 0;
            _downCarryBits = 0;
            while (bits < 128 && _downPixelsLeft > 0)
            {
                var pixel = ReadPixel(_downPsm, _downBp, _downBw, _downX0 + _downX, _downY0 + _downY);
                acc |= (UInt128)pixel << bits;
                if (bits + _downBits > 128)
                {
                    _downCarryBits = bits + _downBits - 128;
                    _downCarry = pixel >> (_downBits - _downCarryBits);
                }

                bits += _downBits;
                _downPixelsLeft--;
                if (++_downX == _downWidth)
                {
                    _downX = 0;
                    _downY++;
                }
            }

            destination[count++] = acc;
            _downQwordsLeft--;
        }

        if (_downQwordsLeft == 0)
        {
            _downActive = false;
        }

        return count;
    }

    /// <summary>Local to local (TRXDIR 2), walking in TRXPOS.DIR's order so overlapping copies behave.</summary>
    private void CopyLocal(int sbp, int sbw, int spsm, int sx, int sy, int dbp, int dbw, int dpsm, int dx, int dy, int width, int height, int dir)
    {
        var yDown = (dir & 1) != 0;
        var xDown = (dir & 2) != 0;
        for (var j = 0; j < height; j++)
        {
            var row = yDown ? height - 1 - j : j;
            for (var i = 0; i < width; i++)
            {
                var col = xDown ? width - 1 - i : i;
                var pixel = ReadPixel(spsm, sbp, sbw, sx + col, sy + row);
                WritePixel(dpsm, dbp, dbw, dx + col, dy + row, pixel);
            }
        }
    }

    private void SaveTransfer(StateWriter writer)
    {
        writer.WriteBool(_upActive);
        writer.WriteInt32(_upBp);
        writer.WriteInt32(_upBw);
        writer.WriteInt32(_upPsm);
        writer.WriteInt32(_upX0);
        writer.WriteInt32(_upY0);
        writer.WriteInt32(_upWidth);
        writer.WriteInt32(_upX);
        writer.WriteInt32(_upY);
        writer.WriteInt64(_upPixelsLeft);
        writer.WriteInt32(_upBits);
        writer.WriteUInt64(_upAcc);
        writer.WriteInt32(_upAccBits);
        writer.WriteBool(_downActive);
        writer.WriteInt32(_downBp);
        writer.WriteInt32(_downBw);
        writer.WriteInt32(_downPsm);
        writer.WriteInt32(_downX0);
        writer.WriteInt32(_downY0);
        writer.WriteInt32(_downWidth);
        writer.WriteInt32(_downX);
        writer.WriteInt32(_downY);
        writer.WriteInt64(_downPixelsLeft);
        writer.WriteInt64(_downQwordsLeft);
        writer.WriteInt32(_downBits);
        writer.WriteUInt32(_downCarry);
        writer.WriteInt32(_downCarryBits);
    }

    private void LoadTransfer(StateReader reader)
    {
        _upActive = reader.ReadBool();
        _upBp = reader.ReadInt32();
        _upBw = reader.ReadInt32();
        _upPsm = reader.ReadInt32();
        _upX0 = reader.ReadInt32();
        _upY0 = reader.ReadInt32();
        _upWidth = reader.ReadInt32();
        _upX = reader.ReadInt32();
        _upY = reader.ReadInt32();
        _upPixelsLeft = reader.ReadInt64();
        _upBits = reader.ReadInt32();
        _upAcc = reader.ReadUInt64();
        _upAccBits = reader.ReadInt32();
        _downActive = reader.ReadBool();
        _downBp = reader.ReadInt32();
        _downBw = reader.ReadInt32();
        _downPsm = reader.ReadInt32();
        _downX0 = reader.ReadInt32();
        _downY0 = reader.ReadInt32();
        _downWidth = reader.ReadInt32();
        _downX = reader.ReadInt32();
        _downY = reader.ReadInt32();
        _downPixelsLeft = reader.ReadInt64();
        _downQwordsLeft = reader.ReadInt64();
        _downBits = reader.ReadInt32();
        _downCarry = reader.ReadUInt32();
        _downCarryBits = reader.ReadInt32();
    }
}
