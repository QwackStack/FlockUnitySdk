using System.IO;
using UnityEngine;

namespace Flock
{
    public static class FlockUtil
    {
        public static readonly string FlockFilePath = Path.Combine(Application.persistentDataPath, "Flock");
        [System.Obsolete("The Windows and Android token stores keep both tokens in one file, TokenData, since 1.86.0; this file is only read once, from an earlier version.")]
        public static readonly string RefreshTokenPath = Path.Combine(FlockUtil.FlockFilePath,"RefreshData");
        [System.Obsolete("The Windows and Android token stores keep both tokens in one file, TokenData, since 1.86.0; this file is only read once, from an earlier version.")]
        public static readonly string AccessTokenPath = Path.Combine(FlockUtil.FlockFilePath,"AccessData");
    }
}