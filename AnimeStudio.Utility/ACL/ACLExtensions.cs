using System;
using ACLLibs;
using System.IO;

namespace AnimeStudio
{
    public static class ACLExtensions
    {
        // 新版 ZZZ 动画 scalar 段的版本号；更早的动画仍是 v02_01_99(8)。
        private const ushort VHoYoAclVersion = 100;

        public static void Process(this ACLClip m_ACLClip, Game game, out float[] values, out float[] times) 
        {
            if (game.Type.IsSRGroup())
            {
                var aclClip = m_ACLClip as MHYACLClip;
                SRACL.DecompressClip(aclClip.m_ClipData, out values, out times);
            }
            else
            {
                switch (m_ACLClip)
                {
                    case GIACLClip giaclClip:
                        DBACL.DecompressTracks(giaclClip.m_ClipData, giaclClip.m_DatabaseData, out values, out times);
                        break;
                    case ZZZACLClip zzzaclClip:
                    {
                        // 旧版动画（例如 Anbi 的战斗动作）的 scalar 段仍是 v02_01_99(8)，ZZZV2 解码器
                        // 只接受 vHoYo(100) 的 scalar 段，遇到其它版本会在原生断言里直接终止进程。
                        // 旧版 ZZZ 解码器可以从整段 m_ClipData 完整解出 transform + scalar 曲线。
                        var scalarData = zzzaclClip.m_ScalarData;
                        if (scalarData != null && scalarData.Length >= 14 && BitConverter.ToUInt16(scalarData, 12) != VHoYoAclVersion)
                            DBACL.DecompressTracks(zzzaclClip.m_ClipData, zzzaclClip.m_databaseData, out values, out times, true);
                        else
                            DBACL.DecompressTracksV2(zzzaclClip.m_TransformData, scalarData, zzzaclClip.m_databaseData, zzzaclClip.m_DatabaseData, out values, out times);
                        break;
                    }
                    case MHYACLClip mhyaclClip:
                        if (game.Type.IsZZZ())
                        {
                            DBACL.DecompressTracks(mhyaclClip.m_ClipData, mhyaclClip.m_databaseData, out values, out times, true);
                        }
                        else
                        {
                            ACL.DecompressClip(mhyaclClip.m_ClipData, out values, out times);
                        }
                        break;
                    default:
                        values = Array.Empty<float>();
                        times = Array.Empty<float>();
                        break;
                }
            }
            if (game.Type.IsZZZ() && m_ACLClip.IsSet &&
                (times.Length == 0 || values.LongLength != (long)times.Length * m_ACLClip.CurveCount))
            {
                throw new InvalidDataException($"ZZZ ACL decoder returned {values.Length} values for {times.Length} frames and {m_ACLClip.CurveCount} curves.");
            }
        }
    }
}
