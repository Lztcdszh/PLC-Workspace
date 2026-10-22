using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using MyPLCFoundationLibrary.Logging;

namespace PLCTesting.Services
{
    /// <summary>
    /// 证书加载器：把配置里的证书路径安全地变成 <see cref="X509Certificate2"/>。
    ///
    /// 支持格式：
    ///   - .pfx / .p12 —— 通常同时含证书与私钥，可带密码（客户端证书首选）
    ///   - .pem        —— 文件里含 "PRIVATE KEY" 就按"证书+私钥"加载，否则只当证书
    ///   - .crt/.cer  —— 先按 PEM 试，失败再按 DER 试（两种编码都常见）
    ///
    /// 所有失败都只记日志并返回 null，**不抛异常**：
    /// 证书配错时的正确表现是"连不上并给出原因"，而不是让整个 MQTT 服务崩掉。
    /// </summary>
    internal static class CertificateLoader
    {
        private static IAppLogger Log => StructuredLogger.Current.ForContext("Tls");

        /// <param name="path">证书路径，支持 %ENV% 环境变量</param>
        /// <param name="password">pfx/p12 的密码，可为空</param>
        /// <param name="what">用于日志的称呼，如 "CA 证书" / "客户端证书"</param>
        public static X509Certificate2 TryLoad(string path, string password, string what)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var full = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!File.Exists(full))
            {
                Log.Warning("TLS：{What} 文件不存在，已忽略：{Path}", what, full);
                return null;
            }

            try
            {
                var ext = Path.GetExtension(full).ToLowerInvariant();
                X509Certificate2 cert;

                switch (ext)
                {
                    case ".pfx":
                    case ".p12":
                        cert = new X509Certificate2(
                            full,
                            string.IsNullOrEmpty(password) ? null : password,
                            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                        break;

                    case ".pem":
                        var pem = File.ReadAllText(full);
                        cert = pem.Contains("PRIVATE KEY", StringComparison.Ordinal)
                            ? X509Certificate2.CreateFromPem(pem)          // 证书 + 私钥同文件
                            : X509Certificate2.CreateFromPemFile(full);    // 只有证书
                        break;

                    default:
                        // .crt / .cer / .der：PEM 与 DER 两种编码都试一遍
                        try
                        {
                            cert = X509Certificate2.CreateFromPemFile(full);
                        }
                        catch
                        {
                            cert = new X509Certificate2(full);
                        }
                        break;
                }

                Log.Information("TLS：已加载{What} Subject={Subject} 有效期至 {NotAfter:yyyy-MM-dd} 含私钥={HasKey}",
                    what, cert.Subject, cert.NotAfter, cert.HasPrivateKey);
                return cert;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TLS：加载{What}失败：{Path}", what, full);
                return null;
            }
        }
    }
}
