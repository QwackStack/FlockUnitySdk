using System.IO;
using UnityEngine;

namespace Flock
{
    public static class FlockUtil
    {
        public static readonly string FlockFilePath = Path.Combine(Application.persistentDataPath, "Flock");
        public static readonly string RefreshTokenPath = Path.Combine(FlockUtil.FlockFilePath,"RefreshData");
        public static readonly string AccessTokenPath = Path.Combine(FlockUtil.FlockFilePath,"AccessData");
    }
}