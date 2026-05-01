// Auto-generated from AprGBemu/Emu_GB/CPU.cs via bulk rename
// (r_X → _x, MEM_r8 → _bus.ReadByte, flagX → _flagX, etc.).
// Hand edits beyond this header should be marked with a comment.
using System;
using AprGb.Cli.Memory;

namespace AprGb.Cli.Cpu;

public sealed partial class LegacyCpu
{
    private void Step()
    {
            //temp var
            byte t1_b, t2_b, t3_b;
            ushort t1_us, t2_us;
            sbyte t1_sb;

            byte opcode = _bus.ReadByte(_pc);
#if debug
            DebugTrace(opcode);
#endif
            _pc++;
            _cycles += mCycleTable[opcode];
            switch (opcode)
            {
                //checked 2014.11.08
                #region 8bit Load
                case 0x06: //LD B,n
                    _b = _bus.ReadByte(_pc++);
                    break;
                case 0x0E: //LD C,n
                    _c = _bus.ReadByte(_pc++);
                    break;
                case 0x16: //LD D,n
                    _d = _bus.ReadByte(_pc++);
                    break;
                case 0x1E: //LD E,n
                    _e = _bus.ReadByte(_pc++);
                    break;
                case 0x26: //LD H,n
                    _h = _bus.ReadByte(_pc++);
                    break;
                case 0x2E: //LD L,n
                    _l = _bus.ReadByte(_pc++);
                    break;
                case 0x7F: //LD A,A                    
                    break;
                case 0x78: //LD A,B
                    _a = _b;
                    break;
                case 0x79: //LD A,C
                    _a = _c;
                    break;
                case 0x7A: //LD A,D
                    _a = _d;
                    break;
                case 0x7B://LD A,E
                    _a = _e;
                    break;
                case 0x7C://LD A,H
                    _a = _h;
                    break;
                case 0x7D://LD A,L
                    _a = _l;
                    break;
                case 0x7E: //LD A,(HL)
                    _a = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x40://LD B,B                    
                    break;
                case 0x41://LD B,C
                    _b = _c;
                    break;
                case 0x42://LD ,B,D
                    _b = _d;
                    break;
                case 0x43://LD B,C
                    _b = _e;
                    break;
                case 0x44://LD B,H
                    _b = _h;
                    break;
                case 0x45://LD B,L
                    _b = _l;
                    break;
                case 0x46: //LD B,(HL)
                    _b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x48:// LD C,B
                    _c = _b;
                    break;
                case 0x49://LD C,C                    
                    break;
                case 0x4A://LD C,D
                    _c = _d;
                    break;
                case 0x4B://LD C,E
                    _c = _e;
                    break;
                case 0x4C: //LD C,H
                    _c = _h;
                    break;
                case 0x4D: //LD C,L
                    _c = _l;
                    break;
                case 0x4E: //LD C,(HL)
                    _c = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x50://LD D,B
                    _d = _b;
                    break;
                case 0x51://LD D,C
                    _d = _c;
                    break;
                case 0x52://LD D,D                    
                    break;
                case 0x53:// LD D,E
                    _d = _e;
                    break;
                case 0x54: //LD D,H
                    _d = _h;
                    break;
                case 0x55://LD D,L
                    _d = _l;
                    break;
                case 0x56: //LD D,(HL)
                    _d = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x58://LD E,B
                    _e = _b;
                    break;
                case 0x59://LD E,C
                    _e = _c;
                    break;
                case 0x5A: //LD E,D
                    _e = _d;
                    break;
                case 0x5B: //LD E,E                    
                    break;
                case 0x5c: //LD E,H
                    _e = _h;
                    break;
                case 0x5D: //LD E,L
                    _e = _l;
                    break;
                case 0x5E: //LD E,(HL)
                    _e = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x60://LD H,B
                    _h = _b;
                    break;
                case 0x61://LD H,C
                    _h = _c;
                    break;
                case 0x62://LD H,D
                    _h = _d;
                    break;
                case 0x63://LD H,E
                    _h = _e;
                    break;
                case 0x64:// LD H,H                    
                    break;
                case 0x65://LD H,L
                    _h = _l;
                    break;
                case 0x66://LD H,(HL)
                    _h = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x68: //LD L,B
                    _l = _b;
                    break;
                case 0x69: // LD L,C
                    _l = _c;
                    break;
                case 0x6A://LD L,D
                    _l = _d;
                    break;
                case 0x6B: //LD L,E
                    _l = _e;
                    break;
                case 0x6C: //LD L,H
                    _l = _h;
                    break;
                case 0x6D://LD L,L                    
                    break;
                case 0x6E: //LD L,(HL)
                    _l = _bus.ReadByte((ushort)(_h << 8 | _l));
                    break;
                case 0x70://LD (HL),B
                    _bus.WriteByte((ushort)(_h << 8 | _l), _b);
                    break;
                case 0x71://LD (HL),C
                    _bus.WriteByte((ushort)(_h << 8 | _l), _c);
                    break;
                case 0x72://LD (HL),D
                    _bus.WriteByte((ushort)(_h << 8 | _l), _d);
                    break;
                case 0x73://LD (HL),E
                    _bus.WriteByte((ushort)(_h << 8 | _l), _e);
                    break;
                case 0x74://LD (HL),H
                    _bus.WriteByte((ushort)(_h << 8 | _l), _h);
                    break;
                case 0x75://LD (HL),L
                    _bus.WriteByte((ushort)(_h << 8 | _l), _l);
                    break;
                case 0x36://LD (HL),n
                    _bus.WriteByte((ushort)(_h << 8 | _l), _bus.ReadByte(_pc++));
                    break;
                case 0x0A: //LD A,(BC)
                    _a = _bus.ReadByte((ushort)(_b << 8 | _c));
                    break;
                case 0x1A://LD A,(DE)
                    _a = _bus.ReadByte((ushort)(_d << 8 | _e));
                    break;
                case 0xFA: //LD A,(nn)
                    _a = _bus.ReadByte((ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8)));
                    break;
                case 0x3E: // LD A,n
                    _a = _bus.ReadByte(_pc++);
                    break;
                case 0x47://LD B,A
                    _b = _a;
                    break;
                case 0x4F://LD C,A
                    _c = _a;
                    break;
                case 0x57://LD D,A
                    _d = _a;
                    break;
                case 0x5F://LD ,E,A
                    _e = _a;
                    break;
                case 0x67://LD H,A
                    _h = _a;
                    break;
                case 0x6F://LD L,A
                    _l = _a;
                    break;
                case 0x02://LD (BC),A
                    _bus.WriteByte((ushort)(_b << 8 | _c), _a);
                    break;
                case 0x12://LD (DE),A
                    _bus.WriteByte((ushort)(_d << 8 | _e), _a);
                    break;
                case 0x77://LD (HL),A
                    _bus.WriteByte((ushort)(_h << 8 | _l), _a);
                    break;
                case 0xEA://LD (nn),A
                    _bus.WriteByte((ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8)), _a);
                    break;
                case 0xF2: //LD A,($FF00+C)
                    _a = _bus.ReadByte((ushort)(0xFF00 | _c));
                    break;
                case 0xE2://LD ($FF00+C),A
                    _bus.WriteByte((ushort)(0xFF00 | _c), _a);
                    break;
                case 0x3A://LD A,(HLD)
                    //fix 11/05
                    t1_us = (ushort)(_h << 8 | _l);
                    t2_us = (ushort)(t1_us - 1);
                    _a = _bus.ReadByte(t1_us);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0x32: //LD (HDL),A
                    //fix 11/05
                    t1_us = (ushort)(_h << 8 | _l);
                    t2_us = (ushort)(t1_us - 1);
                    _bus.WriteByte(t1_us, _a);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0x2A://LD A,(HLI)
                    t1_us = (ushort)(_h << 8 | _l);
                    t2_us = (ushort)(t1_us + 1);
                    _a = _bus.ReadByte(t1_us);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0x22://LD (HLI),A
                    t1_us = (ushort)((_h << 8 | _l) + 1);
                    _bus.WriteByte((ushort)(_h << 8 | _l), _a);
                    _h = (byte)(t1_us >> 8);
                    _l = (byte)(t1_us & 0xFF);
                    break;
                case 0xE0://LD ($FF00+n),A
                    _bus.WriteByte((ushort)(0xFF00 | _bus.ReadByte(_pc++)), _a);
                    break;
                case 0xF0://LD A,($FF00+n)
                    _a = _bus.ReadByte((ushort)(0xFF00 | _bus.ReadByte(_pc++)));
                    break;
                #endregion

                #region 16bit load
                case 0x01: //LD BC,nn
                    _c = _bus.ReadByte(_pc++);
                    _b = _bus.ReadByte(_pc++);
                    break;
                case 0x11://LD DE,nn
                    _e = _bus.ReadByte(_pc++);
                    _d = _bus.ReadByte(_pc++);
                    break;
                case 0x21://LD HL,nn
                    _l = _bus.ReadByte(_pc++);
                    _h = _bus.ReadByte(_pc++);
                    break;
                case 0x31: //LD SP,nn
                    _sp = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    break;
                case 0xF9://LD SP,HL
                    _sp = (ushort)(_h << 8 | _l);
                    break;
                case 0xF8: //LDHL SP,n
                    _flagZ = FlagClear;
                    _flagN = FlagClear;
                    _flagC = FlagClear;
                    _flagH = FlagClear;
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    t1_us = (ushort)(t1_sb + _sp);
                    if (((_sp ^ t1_sb ^ t1_us) & 0x100) == 0x100) _flagC = FlagSet;
                    if (((_sp ^ t1_sb ^ t1_us) & 0x10) == 0x10) _flagH = FlagSet;
                    _h = (byte)(t1_us >> 8);
                    _l = (byte)(t1_us & 0xff);
                    break;
                case 0x08: //LD (nn),SP
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    _bus.WriteByte((ushort)(t1_us + 1), (byte)(_sp >> 8));
                    _bus.WriteByte(t1_us, (byte)(_sp & 0xFF));
                    break;
                case 0xF5: //PUSH AF
                    _bus.WriteByte(--_sp, _a);
                    _bus.WriteByte(--_sp, (byte)(_flagZ << 7 | _flagN << 6 | _flagH << 5 | _flagC << 4));
                    break;
                case 0xC5: //PUSH BC
                    _bus.WriteByte(--_sp, _b);
                    _bus.WriteByte(--_sp, _c);
                    break;
                case 0xD5://PUSH DE
                    _bus.WriteByte(--_sp, _d);
                    _bus.WriteByte(--_sp, _e);
                    break;
                case 0xE5://PUSH HL
                    _bus.WriteByte(--_sp, _h);
                    _bus.WriteByte(--_sp, _l);
                    break;
                case 0xF1://POP AF 
                    // 11/25 fix
                    t1_b = _bus.ReadByte(_sp++);
                    _flagZ = (t1_b & 0x80) >> 7;
                    _flagN = (t1_b & 0x40) >> 6;
                    _flagH = (t1_b & 0x20) >> 5;
                    _flagC = (t1_b & 0x10) >> 4;
                    _a = _bus.ReadByte(_sp++);
                    break;
                case 0xC1://POP BC
                    _c = _bus.ReadByte(_sp++);
                    _b = _bus.ReadByte(_sp++);
                    break;
                case 0xD1://POP DE
                    _e = _bus.ReadByte(_sp++);
                    _d = _bus.ReadByte(_sp++);
                    break;
                case 0xE1://POP HL
                    _l = _bus.ReadByte(_sp++);
                    _h = _bus.ReadByte(_sp++);
                    break;
                #endregion

                #region 8bit ALU
                case 0x87: //ADD A,A
                    _flagN = FlagClear;
                    if (_a + _a > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_a & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _a;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x80://ADD A,B
                    _flagN = FlagClear;
                    if (_a + _b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_b & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x81://ADD A,C
                    _flagN = FlagClear;
                    if (_a + _c > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_c & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _c;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x82://ADD A,D
                    _flagN = FlagClear;
                    if (_a + _d > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_d & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _d;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x83:// ADD A,E
                    _flagN = FlagClear;
                    if (_a + _e > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_e & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _e;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x84://ADD A,H
                    // 11/25 fixed
                    _flagN = FlagClear;
                    if (_a + _h > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_h & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _h;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x85: //ADD A,L
                    _flagN = FlagClear;
                    if (_a + _l > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_l & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += _l;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x86://ADD A,(HL)
                    t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    _flagN = FlagClear;
                    if (_a + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (t1_b & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += t1_b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xC6: //ADD A,n
                    t1_b = _bus.ReadByte(_pc++);
                    _flagN = FlagClear;
                    if (_a + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (t1_b & 0xF) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += t1_b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;

                    break;
                case 0x8F: //ADC A,A
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _a + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_a & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _a);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x88: //ADC A,B
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if ((_a + _b + t1_b) > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if (((_a & 0xF) + (_b & 0xF) + t1_b) > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x89://ADC A,C
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _c + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_c & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _c);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x8A://ADC A,D
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _d + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_d & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _d);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x8B://ADC A,E
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _e + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_e & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _e);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x8C://ADC A,H
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _h + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_h & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _h);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x8D://ADC A,L
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + _l + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (_l & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + _l);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x8E://ADC A,(HL)
                    t2_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + t2_b + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (t2_b & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + t2_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xCE://ADC A,n
                    //{ 
                    t2_b = _bus.ReadByte(_pc++);
                    _flagN = FlagClear;
                    t1_b = (byte)_flagC;
                    if (_a + t2_b + t1_b > 0xFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if ((_a & 0xF) + (t2_b & 0xF) + t1_b > 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    _a += (byte)(t1_b + t2_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x97://SUB A,A
                    _flagC = FlagClear;
                    _flagH = FlagClear;
                    _flagN = FlagSet;
                    _flagZ = FlagSet;
                    _a = 0;
                    break;
                case 0x90://SUB A,B
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x91://SUB A,C
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_c & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_c & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _c;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x92://SUB A,D
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_d & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_d & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _d;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x93://SUB A,E
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_e & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_e & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _e;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x94://SUB A,H
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_h & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_h & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _h;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x95://SUB A,L
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_l & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_l & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= _l;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x96://SUB A,(HL)
                    t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (t1_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (t1_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= t1_b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xD6://SUB A,n
                    t1_b = _bus.ReadByte(_pc++);
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (t1_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (t1_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a -= t1_b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9F: //SBC A,A
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_a & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_a & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _a - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x98://SBC A,B
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_b & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_b & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _b - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x99://SBC A,C
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_c & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_c & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _c - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9A://SBC A,D
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_d & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_d & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _d - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9B://SBC A,E
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_e & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_e & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _e - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9C://SBC A,H
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_h & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_h & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _h - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9D://SBC A,L
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    if ((_a & 0xF) < ((_l & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((_l & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - _l - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x9E://SBC A,(HL)
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    t2_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    if ((_a & 0xF) < ((t2_b & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((t2_b & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - t2_b - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xDE://SBC A,n
                    _flagN = FlagSet;
                    t1_b = (byte)_flagC;
                    t2_b = _bus.ReadByte(_pc++);
                    if ((_a & 0xF) < ((t2_b & 0xF) + t1_b)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < ((t2_b & 0xFF) + t1_b)) _flagC = FlagSet; else _flagC = FlagClear;
                    _a = (byte)(_a - t2_b - t1_b);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA7: //AND A,A
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA0://AND A,B
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA1://AND A,C
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _c;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA2://AND A,D
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _d;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA3://AND A,E
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _e;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA4://AND A,H
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _h;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA5://AND A,L
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _l;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA6://AND A,(HL)
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _bus.ReadByte((ushort)(_h << 8 | _l));
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xE6://AND A,n
                    _flagC = FlagClear;
                    _flagH = FlagSet;
                    _flagN = FlagClear;
                    _a &= _bus.ReadByte(_pc++);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB7://OR A,A
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB0://OR A,B
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB1://OR A,C
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _c;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB2://OR A,D
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _d;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB3://OR A,E
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _e;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB4://OR A,H
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _h;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB5://OR A,L
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _l;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB6://OR A,(HL)
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _bus.ReadByte((ushort)(_h << 8 | _l));
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xF6://OR A,n
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a |= _bus.ReadByte(_pc++);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAF://XOR A,A
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _a;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA8://XOR A,B
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _b;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xA9://XOR A,C
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _c;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAA://XOR A,D
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _d;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAB://XOR A,E
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _e;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAC://XOR A,H
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _h;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAD://XOR A,L
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _l;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xAE://XOR A,(HL)
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _bus.ReadByte((ushort)(_h << 8 | _l));
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xEE: //XOR A,n
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagClear;
                    _a ^= _bus.ReadByte(_pc++);
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBF: //CP A
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_a & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_a & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    _flagZ = FlagSet;
                    break;
                case 0xB8://CP B
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _b) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xB9://CD C
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_c & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_c & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _c) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBA://CP D
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_d & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_d & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _d) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBB://CP E
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_e & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_e & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _e) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBC://CP H
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_h & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_h & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _h) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBD://CP L
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (_l & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (_l & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == _l) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xBE://CP (HL)
                    t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (t1_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (t1_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == t1_b) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0xFE: //CP A,n
                    t1_b = _bus.ReadByte(_pc++);
                    _flagN = FlagSet;
                    if ((_a & 0xF) < (t1_b & 0xF)) _flagH = FlagSet; else _flagH = FlagClear;
                    if ((_a & 0xFF) < (t1_b & 0xFF)) _flagC = FlagSet; else _flagC = FlagClear;
                    if (_a == t1_b) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x3C://INC A
                    _flagN = FlagClear;
                    _a++;
                    if ((_a & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x04://INC B
                    _flagN = FlagClear;
                    _b++;
                    if ((_b & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x0C: //INC C
                    _flagN = FlagClear;
                    _c++;
                    if ((_c & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x14: //INC D
                    _flagN = FlagClear;
                    _d++;
                    if ((_d & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x1C: //INC E
                    _flagN = FlagClear;
                    _e++;
                    if ((_e & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x24: //INC H
                    _flagN = FlagClear;
                    _h++;
                    if ((_h & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x2C: //INC L
                    _flagN = FlagClear;
                    _l++;
                    if ((_l & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x34: //INC (HL)
                    t1_b = (byte)(_bus.ReadByte((ushort)(_h << 8 | _l)));
                    _flagN = FlagClear;
                    t1_b++;
                    if ((t1_b & 0xF) == 0) _flagH = FlagSet; else _flagH = FlagClear;
                    if (t1_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    _bus.WriteByte((ushort)(_h << 8 | _l), t1_b);
                    break;
                case 0x3D: //DEC A
                    _flagN = FlagSet;
                    _a--;
                    if ((_a & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x05://DEC B
                    _flagN = FlagSet;
                    _b--;
                    if ((_b & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x0D://DEC C
                    _flagN = FlagSet;
                    _c--;
                    if ((_c & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x15://DEC D
                    _flagN = FlagSet;
                    _d--;
                    if ((_d & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x1D://DEC E
                    _flagN = FlagSet;
                    _e--;
                    if ((_e & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x25://DEC H
                    _flagN = FlagSet;
                    _h--;
                    if ((_h & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x2D://DEC L
                    _flagN = FlagSet;
                    _l--;
                    if ((_l & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                case 0x35://DEC (HL)
                    t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                    t1_b--;
                    _bus.WriteByte((ushort)(_h << 8 | _l), t1_b);
                    _flagN = FlagSet;
                    if ((t1_b & 0xF) == 0xF) _flagH = FlagSet; else _flagH = FlagClear;
                    if (t1_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    break;
                #endregion

                #region 16bit ALU
                case 0x09: //ADD HL,BC
                    _flagN = FlagClear;
                    t1_us = (ushort)(_b << 8 | _c);
                    t2_us = (ushort)(_h << 8 | _l);
                    if ((t1_us + t2_us) > 0xFFFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if (((t1_us & 0xFFF) + (t2_us & 0XFFF)) > 0xFFF) _flagH = FlagSet; else _flagH = FlagClear;
                    t2_us = (ushort)((t1_us + t2_us) & 0xFFFF);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0x19://ADD HL,DE
                    _flagN = FlagClear;
                    t1_us = (ushort)(_d << 8 | _e);
                    t2_us = (ushort)(_h << 8 | _l);
                    if ((t1_us + t2_us) > 0xFFFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if (((t1_us & 0xFFF) + (t2_us & 0XFFF)) > 0xFFF) _flagH = FlagSet; else _flagH = FlagClear;
                    t2_us = (ushort)((t1_us + t2_us) & 0xFFFF);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0x29://ADD HL,HL
                    _flagN = FlagClear;
                    t1_us = (ushort)(_h << 8 | _l);
                    if ((t1_us + t1_us) > 0xFFFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if (((t1_us & 0xFFF) + (t1_us & 0XFFF)) > 0xFFF) _flagH = FlagSet; else _flagH = FlagClear;
                    t1_us = (ushort)((t1_us + t1_us) & 0xFFFF);
                    _h = (byte)(t1_us >> 8);
                    _l = (byte)(t1_us & 0xFF);
                    break;
                case 0x39://ADD HL,SP
                    _flagN = FlagClear;
                    t1_us = _sp;
                    t2_us = (ushort)(_h << 8 | _l);
                    if ((t1_us + t2_us) > 0xFFFF) _flagC = FlagSet; else _flagC = FlagClear;
                    if (((t1_us & 0xFFF) + (t2_us & 0XFFF)) > 0xFFF) _flagH = FlagSet; else _flagH = FlagClear;
                    t2_us = (ushort)((t1_us + t2_us) & 0xFFFF);
                    _h = (byte)(t2_us >> 8);
                    _l = (byte)(t2_us & 0xFF);
                    break;
                case 0xE8://ADD SP,n
                    _flagZ = FlagClear;
                    _flagN = FlagClear;
                    _flagC = FlagClear;
                    _flagH = FlagClear;
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    int res = _sp + t1_sb;
                    if (((_sp ^ t1_sb ^ (res & 0xffff)) & 0x100) == 0x100) _flagC = FlagSet;
                    if (((_sp ^ t1_sb ^ (res & 0xffff)) & 0x10) == 0x10) _flagH = FlagSet;
                    _sp = (ushort)(_sp + t1_sb);
                    break;
                case 0x03: //INC BC
                    t1_us = (ushort)((_b << 8 | _c) + 1);
                    _b = (byte)(t1_us >> 8);
                    _c = (byte)(t1_us & 0xff);
                    break;
                case 0x13://INC DE
                    t1_us = (ushort)((_d << 8 | _e) + 1);
                    _d = (byte)(t1_us >> 8);
                    _e = (byte)(t1_us & 0xff);
                    break;
                case 0x23://INC HL
                    t1_us = (ushort)((_h << 8 | _l) + 1);
                    _h = (byte)(t1_us >> 8);
                    _l = (byte)(t1_us & 0xff);
                    break;
                case 0x33://INC SP
                    ++_sp;
                    break;
                case 0x0B: //DEC BC
                    t1_us = (ushort)((_b << 8 | _c) - 1);
                    _b = (byte)(t1_us >> 8);
                    _c = (byte)(t1_us & 0xff);
                    break;
                case 0x1B://DEC DE
                    t1_us = (ushort)((_d << 8 | _e) - 1);
                    _d = (byte)(t1_us >> 8);
                    _e = (byte)(t1_us & 0xff);
                    break;
                case 0x2B://DEC HL
                    t1_us = (ushort)((_h << 8 | _l) - 1);
                    _h = (byte)(t1_us >> 8);
                    _l = (byte)(t1_us & 0xff);
                    break;
                case 0x3B://DEC SP
                    --_sp;
                    break;
                #endregion

                #region miscellaneous
                //SWAP move to Rotates & shifts
                case 0x27: //DAA
                    // REF https://github.com/drhelius/Gearboy/blob/master/src/opcodes.cpp  void Processor::OPCode0x27()
                    // DAA 這指令的運作,在GAMEBOY Z80上似乎有自己的特性
                    t1_us = _a;
                    if (_flagN == FlagClear)
                    {
                        if (_flagH == FlagSet || ((t1_us & 0xF) > 9)) t1_us += 0x06;
                        if (_flagC == FlagSet || (t1_us > 0x9F)) t1_us += 0x60;
                    }
                    else
                    {
                        if (_flagH == FlagSet) t1_us = (ushort)((t1_us - 6) & 0xFF);
                        if (_flagC == FlagSet) t1_us -= 0x60;
                    }
                    _flagH = FlagClear;
                    if ((t1_us & 0x100) == 0x100) _flagC = FlagSet;
                    t1_us &= 0xff;
                    if (t1_us == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                    _a = (byte)t1_us;
                    break;
                case 0x2F://CPL
                    _flagN = FlagSet;
                    _flagH = FlagSet;
                    _a = (byte)(~_a);
                    break;
                case 0x3F://CCF
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    if (_flagC == FlagClear) _flagC = FlagSet; else _flagC = FlagClear;
                    break;
                case 0x37://SCF 11/9 fixed
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = FlagSet;
                    break;
                case 0x00: //NOP
                    break;
                case 0x76://HALT
                    halt_cycle = 12;
                    flagHalt = true;
                    break;
                case 0x10://STOP
                    Console.WriteLine("stop: 0x" + _pc.ToString("x2"));
                    break;
                case 0xF3://DI
                    flagIME = false;
                    break;
                case 0xFB://EI — delayed by one instruction (LR35902 spec)
                    _eiDelay = 2;
                    break;
                #endregion
                #region jumps , calls ,restarts , returns
                case 0xc3: //JP nn
                    _pc = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    break;
                case 0xC2: //JP NZ,nn
                    t1_b = _bus.ReadByte(_pc++);
                    t2_b = _bus.ReadByte(_pc++);
                    if (_flagZ == 0)
                    {
                        _cycles += 1;
                        _pc = (ushort)(t2_b << 8 | t1_b);
                    }
                    break;
                case 0xCA://JP Z,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagZ == 1) _pc = t1_us;
                    break;
                case 0xD2://JP NC,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagC == 0)
                    {
                        _cycles += 1;
                        _pc = t1_us;
                    }
                    break;
                case 0xDA://JP C,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagC == 1)
                    {
                        _cycles += 1;
                        _pc = t1_us;
                    }
                    break;
                case 0xE9://JP  (HL)
                    _pc = (ushort)(_h << 8 | _l);
                    break;
                case 0x18://JR n
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    _pc = (ushort)(_pc + t1_sb);
                    break;
                case 0x20: //JR NZ,n (signed byte)
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    if (_flagZ == 0)
                    {
                        _cycles += 1;
                        _pc = (ushort)(_pc + t1_sb);
                    }
                    break;
                case 0x28://JR Z,n
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    if (_flagZ == 1)
                    {
                        _cycles += 1;
                        _pc = (ushort)(_pc + t1_sb);
                    }
                    break;
                case 0x30://JR NC,n
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    if (_flagC == 0)
                    {
                        _cycles += 1;
                        _pc = (ushort)(_pc + t1_sb);
                    }
                    break;
                case 0x38://JR C,n
                    t1_sb = (sbyte)_bus.ReadByte(_pc++);
                    if (_flagC == 1)
                    {
                        _cycles += 1;
                        _pc = (ushort)(_pc + t1_sb);
                    }
                    break;
                case 0xCD: //CALL nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = t1_us;
                    break;
                case 0xC4://CALL NZ,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagZ == 0)
                    {
                        _cycles += 3;
                        _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                        _bus.WriteByte(--_sp, (byte)_pc);
                        _pc = t1_us;
                    }
                    break;
                case 0xCC://CALL Z,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagZ == 1)
                    {
                        _cycles += 3;
                        _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                        _bus.WriteByte(--_sp, (byte)_pc);
                        _pc = t1_us;
                    }
                    break;
                case 0xD4://CALL NC,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8)); ;
                    if (_flagC == 0)
                    {
                        _cycles += 3;
                        _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                        _bus.WriteByte(--_sp, (byte)_pc);
                        _pc = t1_us;
                    }
                    break;
                case 0xDC://CALL C,nn
                    t1_us = (ushort)(_bus.ReadByte(_pc++) | (_bus.ReadByte(_pc++) << 8));
                    if (_flagC == 1)
                    {
                        _cycles += 3;
                        _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                        _bus.WriteByte(--_sp, (byte)_pc);
                        _pc = t1_us;
                    }
                    break;
                case 0xC7: //RST 00H                    
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0;
                    break;
                case 0xCF://RST 08H                    
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x08;
                    break;
                case 0xD7://RST 10H
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x10;
                    break;
                case 0xDF://RST 18H
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x18;
                    break;
                case 0xE7://RST 20H                    
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x20;
                    break;
                case 0xEF://RST 28H
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x28;
                    break;
                case 0xF7://RST 30H                    
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x30;
                    break;
                case 0xFF://RST 38H                    
                    _bus.WriteByte(--_sp, (byte)(_pc >> 8));
                    _bus.WriteByte(--_sp, (byte)_pc);
                    _pc = 0x38;
                    break;
                case 0xC9://RET
                    _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    break;
                case 0xC0://RET NZ                    
                    if (_flagZ == FlagClear)
                    {
                        _cycles += 3;
                        _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    }
                    break;
                case 0xC8://RET Z                    
                    if (_flagZ == FlagSet)
                    {
                        _cycles += 3;
                        _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    }
                    break;
                case 0xD0: //RET NC                    
                    if (_flagC == FlagClear)
                    {
                        _cycles += 3;
                        _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    }
                    break;
                case 0xD8://RET C
                    if (_flagC == FlagSet)
                    {
                        _cycles += 3;
                        _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    }
                    break;
                case 0xD9:
                    _pc = (ushort)(_bus.ReadByte(_sp++) | (_bus.ReadByte(_sp++) << 8));
                    flagIME = true;
                    break;
                #endregion
                #region Rotates & Shifts without CB prefix

                case 0x07://RLCA
                    _flagZ = FlagClear; // Z flag 被清除才是正確的,非官方規格文件描述許多有誤,被搞死.. orz...
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = ((_a >> 7) & 1);
                    _a = (byte)((_a << 1) | _flagC);
                    break;
                case 0x17://RLA
                    _flagZ = FlagClear;
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    t1_b = (byte)_flagC;
                    _flagC = (_a >> 7 & 1);
                    _a <<= 1;
                    _a |= t1_b;
                    break;
                case 0x0F://RRCA
                    _flagZ = FlagClear;
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    _flagC = (_a & 1);
                    _a = (byte)((_a >> 1) | (_flagC << 7));
                    break;
                case 0x1F://RRA
                    _flagZ = FlagClear;
                    _flagN = FlagClear;
                    _flagH = FlagClear;
                    t1_b = (byte)_flagC;
                    _flagC = (_a & 1);
                    _a = (byte)((_a >> 1) | (t1_b << 7));
                    break;
                #endregion

                #region Opcode with 0xCB
                case 0xCb:
                    byte cb_code = _bus.ReadByte(_pc++);
                    _cycles += cbMCycleTable[cb_code];
                    byte b = (byte)((cb_code & 0x38) >> 3);
                    byte reg = (byte)(cb_code & 7);
                    byte cb_op1 = (byte)(cb_code & 0xC0);

                    #region Bit opcode
                    if (cb_op1 == 0x40) //BIT
                    {
                        _flagN = FlagClear;
                        _flagH = FlagSet;
                        switch (reg)
                        {
                            case 0:
                                if ((byte)((_b >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 1:
                                if ((byte)((_c >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 2:
                                if ((byte)((_d >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 3:
                                if ((byte)((_e >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 4:
                                if ((byte)((_h >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 5:
                                if ((byte)((_l >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 6:
                                if (((_bus.ReadByte((ushort)(_h << 8 | _l)) >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                            case 7:
                                if ((byte)((_a >> b) & 1) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                break;
                        }
                        break;
                    }
                    else if (cb_op1 == 0xC0) //SET
                    {
                        switch (reg)
                        {
                            case 0:
                                _b = (byte)(_b | (1 << b));
                                break;
                            case 1:
                                _c = (byte)(_c | (1 << b));
                                break;
                            case 2:
                                _d = (byte)(_d | (1 << b));
                                break;
                            case 3:
                                _e = (byte)(_e | (1 << b));
                                break;
                            case 4:
                                _h = (byte)(_h | (1 << b));
                                break;
                            case 5:
                                _l = (byte)(_l | (1 << b));
                                break;
                            case 6:
                                _bus.WriteByte((ushort)(_h << 8 | _l), (byte)(_bus.ReadByte((ushort)(_h << 8 | _l)) | (1 << b)));
                                break;
                            case 7:
                                _a = (byte)(_a | (1 << b));
                                break;
                        }
                        break;
                    }
                    else if (cb_op1 == 0x80) //RES
                    {
                        switch (reg)
                        {
                            case 0:
                                _b = (byte)(_b & ~(1 << b));
                                break;
                            case 1:
                                _c = (byte)(_c & ~(1 << b));
                                break;
                            case 2:
                                _d = (byte)(_d & ~(1 << b));
                                break;
                            case 3:
                                _e = (byte)(_e & ~(1 << b));
                                break;
                            case 4:
                                _h = (byte)(_h & ~(1 << b));
                                break;
                            case 5: _l = (byte)(_l & ~(1 << b));
                                break;
                            case 6:
                                _bus.WriteByte((ushort)(_h << 8 | _l), (byte)(_bus.ReadByte((ushort)(_h << 8 | _l)) & ~(1 << b)));
                                break;
                            case 7:
                                _a = (byte)(_a & ~(1 << b));
                                break;
                        }
                        break;
                    }
                    else // 0x00
                    {
                        byte cb_op2 = (byte)((cb_code & 0x38) >> 3);

                        _flagN = FlagClear;
                        _flagH = FlagClear;

                        switch (cb_op2)
                        {
                            case 0://RLC
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            _flagC = (_b >> 7 & 1);
                                            _b <<= 1;
                                            _b |= (byte)_flagC;
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _flagC = (_c >> 7 & 1);
                                            _c <<= 1;
                                            _c |= (byte)_flagC;
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _flagC = (_d >> 7 & 1);
                                            _d <<= 1;
                                            _d |= (byte)_flagC;
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _flagC = (_e >> 7 & 1);
                                            _e <<= 1;
                                            _e |= (byte)_flagC;
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _flagC = (_h >> 7 & 1);
                                            _h <<= 1;
                                            _h |= (byte)_flagC;
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _flagC = (_l >> 7 & 1);
                                            _l <<= 1;
                                            _l |= (byte)_flagC;
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                                            _flagC = (t1_b >> 7 & 1);
                                            t1_b <<= 1;
                                            t1_b |= (byte)_flagC;
                                            _bus.WriteByte((ushort)(_h << 8 | _l), t1_b);
                                            if (t1_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            _flagC = (_a >> 7 & 1);
                                            _a <<= 1;
                                            _a |= (byte)_flagC;
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 1://RRC
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            _flagC = (_b & 1);
                                            _b = (byte)((_b >> 1) | (_flagC << 7));
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _flagC = (_c & 1);
                                            _c = (byte)((_c >> 1) | (_flagC << 7));
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _flagC = (_d & 1);
                                            _d = (byte)((_d >> 1) | (_flagC << 7));
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _flagC = (_e & 1);
                                            _e = (byte)((_e >> 1) | (_flagC << 7));
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _flagC = (_h & 1);
                                            _h = (byte)((_h >> 1) | (_flagC << 7));
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _flagC = (_l & 1);
                                            _l = (byte)((_l >> 1) | (_flagC << 7));
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_us = (ushort)(_h << 8 | _l);
                                            _flagC = (_bus.ReadByte(t1_us) & 1);
                                            _bus.WriteByte(t1_us, (byte)((_bus.ReadByte(t1_us) >> 1) | (_flagC << 7)));
                                            if (_bus.ReadByte(t1_us) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            _flagC = (_a & 1);
                                            _a = (byte)((_a >> 1) | (_flagC << 7));
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 2://RL
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_b >> 7 & 1);
                                            _b = (byte)(_b << 1 | t1_b);
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_c >> 7 & 1);
                                            _c = (byte)(_c << 1 | t1_b);
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_d >> 7 & 1);
                                            _d = (byte)(_d << 1 | t1_b);
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_e >> 7 & 1);
                                            _e = (byte)(_e << 1 | t1_b);
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_h >> 7 & 1);
                                            _h = (byte)(_h << 1 | t1_b);
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_l >> 7 & 1);
                                            _l = (byte)(_l << 1 | t1_b);
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_us = (ushort)(_h << 8 | _l);
                                            t2_b = (byte)_flagC;
                                            _flagC = (_bus.ReadByte(t1_us) >> 7 & 1);
                                            _bus.WriteByte(t1_us, (byte)(_bus.ReadByte(t1_us) << 1 | t2_b));
                                            if (_bus.ReadByte(t1_us) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_a >> 7 & 1);
                                            _a = (byte)(_a << 1 | t1_b);
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 3://RR
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_b & 1);
                                            _b = (byte)((_b >> 1) | (t1_b << 7));
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_c & 1);
                                            _c = (byte)((_c >> 1) | (t1_b << 7));
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_d & 1);
                                            _d = (byte)((_d >> 1) | (t1_b << 7));
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_e & 1);
                                            _e = (byte)((_e >> 1) | (t1_b << 7));
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_h & 1);
                                            _h = (byte)((_h >> 1) | (t1_b << 7));
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_l & 1);
                                            _l = (byte)((_l >> 1) | (t1_b << 7));
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_b = _bus.ReadByte((ushort)(_h << 8 | _l));
                                            t2_b = (byte)_flagC;
                                            _flagC = (t1_b & 1);
                                            t1_b = (byte)((t1_b >> 1) | (t2_b << 7));
                                            _bus.WriteByte((ushort)(_h << 8 | _l), t1_b);
                                            if (t1_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            t1_b = (byte)_flagC;
                                            _flagC = (_a & 1);
                                            _a = (byte)((_a >> 1) | (t1_b << 7));
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 4://SLA
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            _flagC = (_b >> 7);
                                            _b <<= 1;
                                            _b &= 0xFE;
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _flagC = (_c >> 7);
                                            _c <<= 1;
                                            _c &= 0xFE;
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _flagC = (_d >> 7);
                                            _d <<= 1;
                                            _d &= 0xFE;
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _flagC = (_e >> 7);
                                            _e <<= 1;
                                            _e &= 0xFE;
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _flagC = (_h >> 7);
                                            _h <<= 1;
                                            _h &= 0xFE;
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _flagC = (_l >> 7);
                                            _l <<= 1;
                                            _l &= 0xFE;
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_us = (ushort)(_h << 8 | _l);
                                            _flagC = (_bus.ReadByte(t1_us) >> 7);
                                            _bus.WriteByte(t1_us, (byte)(_bus.ReadByte(t1_us) << 1));
                                            _bus.WriteByte(t1_us, (byte)(_bus.ReadByte(t1_us) & 0xFE));
                                            if (_bus.ReadByte(t1_us) == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            _flagC = (_a >> 7);
                                            _a <<= 1;
                                            _a &= 0xFE;
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 5://SRA
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            _flagC = (_b & 1);
                                            t1_b = (byte)(_b >> 7);
                                            _b = (byte)((_b >> 1) | (t1_b << 7));
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _flagC = (_c & 1);
                                            t1_b = (byte)(_c >> 7);
                                            _c = (byte)((_c >> 1) | (t1_b << 7));
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _flagC = (_d & 1);
                                            t1_b = (byte)(_d >> 7);
                                            _d = (byte)((_d >> 1) | (t1_b << 7));
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _flagC = (_e & 1);
                                            t1_b = (byte)(_e >> 7);
                                            _e = (byte)((_e >> 1) | (t1_b << 7));
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _flagC = (_h & 1);
                                            t1_b = (byte)(_h >> 7);
                                            _h = (byte)((_h >> 1) | (t1_b << 7));
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _flagC = (_l & 1);
                                            t1_b = (byte)(_l >> 7);
                                            _l = (byte)((_l >> 1) | (t1_b << 7));
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_us = (ushort)(_h << 8 | _l);
                                            t2_b = _bus.ReadByte(t1_us);
                                            _flagC = (t2_b & 1);
                                            t3_b = (byte)(t2_b >> 7);
                                            t2_b = (byte)((t2_b >> 1) | (t3_b << 7));
                                            if (t2_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            _bus.WriteByte(t1_us, t2_b);
                                            break;
                                        case 7:
                                            _flagC = (_a & 1);
                                            t1_b = (byte)(_a >> 7);
                                            _a = (byte)((_a >> 1) | (t1_b << 7));
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 6://SWAP
                                {
                                    _flagC = FlagClear;
                                    switch (reg)
                                    {
                                        case 0:
                                            _b = (byte)(((_b & 0xF) << 4) | ((_b & 0xF0) >> 4));
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _c = (byte)(((_c & 0xF) << 4) | ((_c & 0xF0) >> 4));
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _d = (byte)(((_d & 0xF) << 4) | ((_d & 0xF0) >> 4));
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _e = (byte)(((_e & 0xF) << 4) | ((_e & 0xF0) >> 4));
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _h = (byte)(((_h & 0xF) << 4) | ((_h & 0xF0) >> 4));
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _l = (byte)(((_l & 0xF) << 4) | ((_l & 0xF0) >> 4));
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_b = (byte)(((_bus.ReadByte((ushort)(_h << 8 | _l)) & 0xF) << 4) | ((_bus.ReadByte((ushort)(_h << 8 | _l)) & 0xF0) >> 4));
                                            _bus.WriteByte((ushort)(_h << 8 | _l), (byte)(((_bus.ReadByte((ushort)(_h << 8 | _l)) & 0xF) << 4) | ((_bus.ReadByte((ushort)(_h << 8 | _l)) & 0xF0) >> 4)));
                                            if (t1_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 7:
                                            _a = (byte)(((_a & 0xF) << 4) | ((_a & 0xF0) >> 4));
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                            case 7://SRL
                                {
                                    switch (reg)
                                    {
                                        case 0:
                                            _flagC = (_b & 1);
                                            _b = (byte)((_b >> 1) & 0x7f);
                                            if (_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 1:
                                            _flagC = (_c & 1);
                                            _c = (byte)((_c >> 1) & 0x7f);
                                            if (_c == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 2:
                                            _flagC = (_d & 1);
                                            _d = (byte)((_d >> 1) & 0x7f);
                                            if (_d == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 3:
                                            _flagC = (_e & 1);
                                            _e = (byte)((_e >> 1) & 0x7f);
                                            if (_e == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 4:
                                            _flagC = (_h & 1);
                                            _h = (byte)((_h >> 1) & 0x7f);
                                            if (_h == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 5:
                                            _flagC = (_l & 1);
                                            _l = (byte)((_l >> 1) & 0x7f);
                                            if (_l == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                        case 6:
                                            t1_us = (ushort)(_h << 8 | _l);
                                            t2_b = _bus.ReadByte(t1_us);
                                            _flagC = (t2_b & 1);
                                            t2_b = (byte)((t2_b >> 1) & 0x7f);
                                            if (t2_b == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            _bus.WriteByte(t1_us, t2_b);
                                            break;
                                        case 7:
                                            _flagC = (_a & 1);
                                            _a = (byte)((_a >> 1) & 0x7f);
                                            if (_a == 0) _flagZ = FlagSet; else _flagZ = FlagClear;
                                            break;
                                    }
                                }
                                break;
                        }
                    }
                    #endregion
                    break;
                #endregion
                default:
                    throw new InvalidOperationException($"unknown opcode 0x{opcode:X2} at PC=0x{_pc - 1:X4}");
                    break;
            }
            _cycles *= 4;

            if (DMA_CYCLE)
            {
                //Console.WriteLine("dma");
                _cycles += 671;
                DMA_CYCLE = false;
            }
    }
}
