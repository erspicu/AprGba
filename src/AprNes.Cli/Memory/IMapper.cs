namespace AprNes.Cli.Memory
{
    /// <summary>
    /// NES cartridge mapper interface — ported from erspicu/AprNes commit fcbbb23.
    ///
    /// The CPU memory bus delegates all $4020-$FFFF accesses to the active mapper,
    /// and the PPU delegates all $0000-$1FFF (pattern table) accesses here as well.
    /// Concrete mappers (NROM/MMC1/UxROM/...) implement bank switching, CHR-RAM,
    /// and any cartridge-side registers (e.g. $8000 writes for MMC1).
    /// </summary>
    public interface IMapper
    {
        /// <summary>
        /// Initialise the mapper from raw PRG/CHR ROM data. Called once after
        /// the iNES header has been parsed, before any CPU/PPU access happens.
        /// If <paramref name="chrRom"/> is empty the cartridge uses CHR-RAM and
        /// the mapper must allocate its own 8KB writable CHR buffer.
        /// </summary>
        void Reset(byte[] prgRom, byte[] chrRom);

        /// <summary>
        /// CPU bus read for the cartridge address space. The bus calls this for
        /// $4020-$FFFF; mappers typically only meaningfully respond for
        /// $6000-$7FFF (PRG-RAM/SRAM) and $8000-$FFFF (PRG-ROM banks).
        /// </summary>
        byte CpuRead(ushort addr);

        /// <summary>
        /// CPU bus write for the cartridge address space. For most mappers,
        /// $8000-$FFFF writes go to bank-select registers (mapper-specific),
        /// while $6000-$7FFF writes update PRG-RAM if present.
        /// </summary>
        void CpuWrite(ushort addr, byte value);

        /// <summary>
        /// PPU bus read for the pattern table region ($0000-$1FFF). Reads
        /// either CHR-ROM or CHR-RAM depending on the cartridge type and
        /// current CHR bank state.
        /// </summary>
        byte PpuRead(ushort addr);

        /// <summary>
        /// PPU bus write for the pattern table region ($0000-$1FFF). Only
        /// meaningful for cartridges that ship with CHR-RAM (CHR-ROM games
        /// silently drop the write).
        /// </summary>
        void PpuWrite(ushort addr, byte value);
    }
}
