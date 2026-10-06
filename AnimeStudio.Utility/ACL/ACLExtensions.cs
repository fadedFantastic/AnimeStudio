using System;
using ACLLibs;
using System.IO;

namespace AnimeStudio
{
    public static class ACLExtensions
    {
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
                        DBACL.DecompressTracksV2(zzzaclClip.m_TransformData, zzzaclClip.m_ScalarData, zzzaclClip.m_databaseData, zzzaclClip.m_DatabaseData, out values, out times);
                        break;
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
