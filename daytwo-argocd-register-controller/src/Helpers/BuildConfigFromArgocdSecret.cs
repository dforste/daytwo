﻿using k8s.Models;
using k8s;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;

namespace daytwo.Helpers
{
    public partial class Main
    {
        public static KubernetesClientConfiguration BuildConfigFromArgocdSecret(V1Secret secret)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            KubernetesClientConfiguration kubeconfig = new KubernetesClientConfiguration();

            try
            {
                // form a kubeconfig via the argocd secret
                Console.WriteLine("- form kubeconfig from argocd cluster secret ...");

                // we have a cluster secret, check its name/server
                data.Add("name", Encoding.UTF8.GetString(secret.Data["name"], 0, secret.Data["name"].Length));
                data.Add("server", Encoding.UTF8.GetString(secret.Data["server"], 0, secret.Data["server"].Length));
                data.Add("config", Encoding.UTF8.GetString(secret.Data["config"], 0, secret.Data["config"].Length));

                Console.WriteLine("  -   name: " + data["name"]);
                Console.WriteLine("  - server: " + data["server"]);
                //Console.WriteLine("  - config: " + data["config"]);

                // parse kubeconfig json data from argocd secret
                //Console.WriteLine("  - 1");
                JsonElement o = JsonSerializer.Deserialize<JsonElement>(data["config"]);

                // form kubeconfig using values from argocd secret
                //Console.WriteLine("  - 2");
                kubeconfig.Host = data["server"];
                //Console.WriteLine("  - 3");
                kubeconfig.SkipTlsVerify = o.GetProperty("tlsClientConfig").GetProperty("insecure").GetBoolean();
                //Console.WriteLine("  - 4");
                kubeconfig.ClientCertificateData = o.GetProperty("tlsClientConfig").GetProperty("certData").GetString();
                //Console.WriteLine("  - 5");
                kubeconfig.ClientCertificateKeyData = o.GetProperty("tlsClientConfig").GetProperty("keyData").GetString();
                // convert caData into an x509 cert & add
                //Console.WriteLine("  - 6");
                kubeconfig.SslCaCerts = new X509Certificate2Collection();
                //Console.WriteLine("  - 7");
                if (!kubeconfig.SkipTlsVerify)
                {
                    kubeconfig.SslCaCerts.Add(
                            X509Certificate2.CreateFromPem(
                                Base64Decode(o.GetProperty("tlsClientConfig").GetProperty("caData").GetString()).AsSpan()
                        ));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }

            return kubeconfig;
        }

    }
}
