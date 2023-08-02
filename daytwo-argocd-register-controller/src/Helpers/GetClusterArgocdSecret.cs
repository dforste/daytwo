﻿using daytwo;
using k8s;
using k8s.Models;
using System.Text;

namespace daytwo.Helpers
{
    public partial class Main
    {
        public static V1Secret? GetClusterArgocdSecret(string clusterName, string? managementCluster = null)
        {
            //Console.WriteLine("- GetClusterSecret, clusterName: "+ clusterName);
            V1SecretList secrets = Globals.service.kubeclient.ListNamespacedSecret(Globals.service.argocdNamespace);

            //Console.WriteLine("- argocd cluster secrets:");
            foreach (V1Secret secret in secrets)
            {
                // is there a label indicating this is a cluster secret?
                if (secret.Labels() == null)
                {
                    continue;
                }
                if (!secret.Labels().TryGetValue("argocd.argoproj.io/secret-type", out var value))
                {
                    continue;
                }
                if (value != "cluster")
                {
                    continue;
                }
                // is this secret associated with the specified management cluster?
                if (managementCluster != null)
                {
                    string? tmp = secret.GetAnnotation("daytwo.aarr.xyz/management-cluster");
                    if (managementCluster != tmp)
                    {
                        Console.WriteLine("cluster not managed by daytwo, skipping");
                        continue;
                    }
                }

                // is this the cluster we are looking for?
                string name = Encoding.UTF8.GetString(secret.Data["name"], 0, secret.Data["name"].Length);
                //Console.WriteLine("  - name: " + name +", tkcName: "+ tkc.Metadata.Name);
                if (name != clusterName)
                {
                    continue;
                }

                // secret located
                return secret;
            }

            return null;
        }
    }
}
