#if UNITY_ANDROID
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Flock.Auth
{
    /// <summary>
    /// Persists tokens encrypted with an AES-256-GCM key held in the Android
    /// Keystore (alias <c>Flock.TokenKey</c>). The key never leaves the keystore —
    /// the OS performs encrypt/decrypt and the SDK only sees plaintext at the
    /// boundary. File format on disk is <c>IV(12B) || ciphertext+tag</c>.
    /// </summary>
    public class AndroidTokenStore : ITokenStore
    {
        private const string KeyAlias = "Flock.TokenKey";
        private const string KeystoreProvider = "AndroidKeyStore";
        private const string Transformation = "AES/GCM/NoPadding";
        private const int IvLength = 12;
        private const int TagLengthBits = 128;
        // KeyProperties.PURPOSE_ENCRYPT (1) | PURPOSE_DECRYPT (2)
        private const int PurposeEncryptDecrypt = 3;
        private const int CipherEncryptMode = 1;
        private const int CipherDecryptMode = 2;

        public override void Save(string accessToken, string refreshToken)
        {
            Directory.CreateDirectory(FlockUtil.FlockFilePath);
            File.WriteAllBytes(FlockUtil.AccessTokenPath, Encrypt(accessToken));
            File.WriteAllBytes(FlockUtil.RefreshTokenPath, Encrypt(refreshToken));
        }

        public override StoredTokens Load()
        {
            if (!File.Exists(FlockUtil.AccessTokenPath) || !File.Exists(FlockUtil.RefreshTokenPath))
                return null;
            return new StoredTokens
            {
                AccessToken = Decrypt(File.ReadAllBytes(FlockUtil.AccessTokenPath)),
                RefreshToken = Decrypt(File.ReadAllBytes(FlockUtil.RefreshTokenPath))
            };
        }

        public override void Clear()
        {
            if (File.Exists(FlockUtil.AccessTokenPath)) File.Delete(FlockUtil.AccessTokenPath);
            if (File.Exists(FlockUtil.RefreshTokenPath)) File.Delete(FlockUtil.RefreshTokenPath);
        }

        private static byte[] Encrypt(string token)
        {
            byte[] plain = Encoding.UTF8.GetBytes(token);
            using (AndroidJavaObject key = LoadOrCreateKey())
            using (AndroidJavaObject cipher = new AndroidJavaClass("javax.crypto.Cipher")
                .CallStatic<AndroidJavaObject>("getInstance", Transformation))
            {
                cipher.Call("init", CipherEncryptMode, key);
                byte[] iv = cipher.Call<byte[]>("getIV");
                byte[] ct = cipher.Call<byte[]>("doFinal", plain);
                byte[] result = new byte[iv.Length + ct.Length];
                Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
                Buffer.BlockCopy(ct, 0, result, iv.Length, ct.Length);
                return result;
            }
        }

        private static string Decrypt(byte[] data)
        {
            byte[] iv = new byte[IvLength];
            byte[] ct = new byte[data.Length - IvLength];
            Buffer.BlockCopy(data, 0, iv, 0, IvLength);
            Buffer.BlockCopy(data, IvLength, ct, 0, ct.Length);

            using (AndroidJavaObject key = LoadOrCreateKey())
            using (AndroidJavaObject spec = new AndroidJavaObject(
                "javax.crypto.spec.GCMParameterSpec", TagLengthBits, iv))
            using (AndroidJavaObject cipher = new AndroidJavaClass("javax.crypto.Cipher")
                .CallStatic<AndroidJavaObject>("getInstance", Transformation))
            {
                cipher.Call("init", CipherDecryptMode, key, spec);
                byte[] plain = cipher.Call<byte[]>("doFinal", ct);
                return Encoding.UTF8.GetString(plain);
            }
        }

        private static AndroidJavaObject LoadOrCreateKey()
        {
            using (AndroidJavaObject ks = new AndroidJavaClass("java.security.KeyStore")
                .CallStatic<AndroidJavaObject>("getInstance", KeystoreProvider))
            {
                ks.Call("load", new object[] { null });

                if (!ks.Call<bool>("containsAlias", KeyAlias))
                {
                    Debug.Log("[Flock SDK] AndroidTokenStore: generating Android Keystore key (one-time)");
                    using (AndroidJavaObject builder = new AndroidJavaObject(
                        "android.security.keystore.KeyGenParameterSpec$Builder", KeyAlias, PurposeEncryptDecrypt))
                    {
                        builder.Call<AndroidJavaObject>("setBlockModes", (object)new[] { "GCM" }).Dispose();
                        builder.Call<AndroidJavaObject>("setEncryptionPaddings", (object)new[] { "NoPadding" }).Dispose();
                        builder.Call<AndroidJavaObject>("setKeySize", 256).Dispose();
                        using (AndroidJavaObject spec = builder.Call<AndroidJavaObject>("build"))
                        using (AndroidJavaObject kg = new AndroidJavaClass("javax.crypto.KeyGenerator")
                            .CallStatic<AndroidJavaObject>("getInstance", "AES", KeystoreProvider))
                        {
                            kg.Call("init", spec);
                            kg.Call<AndroidJavaObject>("generateKey").Dispose();
                        }
                    }
                }

                // Caller takes ownership; the returned key wrapper is disposed by the caller's using block.
                return ks.Call<AndroidJavaObject>("getKey", KeyAlias, null);
            }
        }
    }
}
#endif
