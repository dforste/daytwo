﻿using daytwo;
using k8s.Models;
using k8s;
using System.Text.Json;
using daytwo.K8sHelpers;
using daytwo.CustomResourceDefinitions;
using daytwo.crd.tanzukubernetescluster;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Buffers.Text;
using Microsoft.AspNetCore.DataProtection;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;
using static System.Net.Mime.MediaTypeNames;
using daytwo.crd.cluster;
using System.Collections.ObjectModel;
using System.Xml.Linq;

namespace gge.K8sControllers
{
    public class ClusterK8sController
    {
        static string api = "cluster";
        static string group = "cluster.x-k8s.io";
        static string version = "v1beta1";
        static string plural = api + "s";

        static GenericClient generic = null;// new GenericClient(Globals.service.kubeclient, group, version, plural);
        static Kubernetes kubeclient = null;
        static KubernetesClientConfiguration kubeconfig = null;


        // providers here as they are associated with each management cluster
        public List<ProviderK8sController> providers = new List<ProviderK8sController>();

        public async Task Listen()
        {
            // locate the provisioning cluster argocd secret
            V1Secret? secret = GetClusterArgocdSecret(Environment.GetEnvironmentVariable("MANAGEMENT_CLUSTERS"));
            // use secret to create kubeconfig
            kubeconfig = BuildConfigFromArgocdSecret(secret);
            // use kubeconfig to create client
            kubeclient = new Kubernetes(kubeconfig);

            //
            generic = new GenericClient(kubeclient, group, version, plural);

            // Enforce only processing one watch event at a time
            SemaphoreSlim semaphore;


            // Watch is a tcp connection therefore it can drop, use a while loop to restart as needed.
            while (true)
            {
                // Prep semaphore (reset in case of exception)
                semaphore = new SemaphoreSlim(1);

                Console.WriteLine(DateTime.UtcNow +" (" + api +") Listen begins ...");
                try
                {
                    await foreach (var (type, item) in generic.WatchNamespacedAsync<CrdCluster>(""))
                    {
                        Console.WriteLine("");
                        Console.WriteLine("(event) [" + type + "] " + plural + "." + group + "/" + version + ": " + item.Metadata.Name);

                        // Acquire Semaphore
                        semaphore.Wait(Globals.cancellationToken);
                        //Console.WriteLine("[" + item.Metadata.Name + "]");

                        // Handle event type
                        switch (type)
                        {
                            case WatchEventType.Added:
                                await ProcessAdded(item);
                                break;
                            //case WatchEventType.Bookmark:
                            //    break;
                            case WatchEventType.Deleted:
                                await ProcessDeleted(item);
                                break;
                            //case WatchEventType.Error:
                            //    break;
                            case WatchEventType.Modified:
                                await ProcessModified(item);
                                break;
                        }

                        // Release semaphore
                        //Console.WriteLine("done.");
                        semaphore.Release();
                    }
                }
                catch (k8s.Autorest.HttpOperationException ex)
                {
                    Console.WriteLine("Exception? " + ex);
                    switch (ex.Response.StatusCode)
                    {
                        // crd is missing, sleep to avoid an error loop
                        case System.Net.HttpStatusCode.NotFound:
                            Console.WriteLine("crd is missing, pausing for a second before retrying");
                            Thread.Sleep(1000);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine("Exception occured while performing 'watch': " + ex);
                }
            }
        }

        public async Task ProcessAdded(CrdCluster tkc)
        {
            ProcessModified(tkc);
        }
        public async Task ProcessModified(CrdCluster cluster)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            string patchStr = string.Empty;

            Console.WriteLine("  - namespace: " + cluster.Namespace() + ", cluster: " + cluster.Name());

            // is this cluster in a ready state?
            if (!(
                (cluster.Status != null)
                && (cluster.Status.phase == "Provisioned")
                && cluster.Status.infrastructureReady
                && cluster.Status.controlPlaneReady
                ))
            {
                // cluster not yet ready
                Console.WriteLine("    - cluster not ready yet");

                return;
            }

            // has this cluster been added to argocd?
            V1Secret? tmp = GetClusterArgocdSecret(cluster.Name());

            if (tmp != null)
            {
                // timestamp was old technique, using resourceVersion now
                //Console.WriteLine($"    -  cluster yaml timestamp: {cluster.Metadata.CreationTimestamp}");
                //Console.WriteLine($"    - argocd secret timestamp: {tmp.Metadata.CreationTimestamp}");
                Console.WriteLine($"    -          cluster yaml resourceVersion: {cluster.Metadata.ResourceVersion}");
                try
                {
                    Console.WriteLine($"    - argocd secret cluster resourceVersion: {tmp.Metadata.EnsureAnnotations()["daytwo.aarr.xyz/resourceVersion"]}");
                }
                catch
                {
                    Console.WriteLine($"    - argocd secret cluster resourceVersion: daytwo annotation missing, ignoring cluster");
                    return;
                }
            }

            // if cluster yaml is newer then secret, then we re-add to argocd
            if (tmp == null)
            {
                Console.WriteLine("      - add cluster to argocd");

                // get new cluster admin kubeconfig
                KubernetesClientConfiguration tmpkubeconfig = await GetClusterKubeConfig(cluster.Name(), cluster.Namespace());

                // add new cluster to argocd

                // acquire argocd cluster secret to so we can add annotation and labels
                tmp = GetClusterArgocdSecret(cluster.Name());
                if (tmp == null)
                {
                    Console.WriteLine("unable to add argocd secret");
                    return;
                }

                // store cluster resourceVersion, we use this later to check for changes
                tmp.SetAnnotation("daytwo.aarr.xyz/resourceVersion", cluster.Metadata.ResourceVersion);

                // set cluster name label
                tmp.SetLabel("daytwo.aarr.xyz/name", cluster.Name());
                tmp.SetLabel("daytwo.aarr.xyz/management-cluster", Environment.GetEnvironmentVariable("MANAGEMENT_CLUSTERS"));

                /*
                // copy over all labels
                foreach (var next in cluster.Labels())
                {
                    tmp.SetLabel(next.Key, next.Value);
                }
                */

                //
                Globals.service.kubeclient.CoreV1.PatchNamespacedSecret(
                    new V1Patch(tmp, V1Patch.PatchType.MergePatch), tmp.Name(), tmp.Namespace());
            }
            // has the cluster resourceVersion changed since we last updated?  if so, update argocd secret
            else if (cluster.Metadata.ResourceVersion != tmp.Metadata.EnsureAnnotations()["daytwo.aarr.xyz/resourceVersion"])
            //else if (DateTime.Compare((DateTime)cluster.Metadata.CreationTimestamp, (DateTime)tmp.Metadata.CreationTimestamp) > 0)
            {
                Console.WriteLine("      - update argocd cluster secret");

                /*
                // remove cluster from argocd
                await ProcessDeleted(cluster);
                */

                // get new cluster admin kubeconfig
                KubernetesClientConfiguration tmpkubeconfig = await GetClusterKubeConfig(cluster.Name(), cluster.Namespace());

                // add new cluster to argocd

                // acquire argocd cluster secret to so we can add annotation and labels
                tmp = GetClusterArgocdSecret(cluster.Name());
                if (tmp == null)
                {
                    Console.WriteLine("unable to add argocd secret");
                    return;
                }

                // store cluster resourceVersion, we use this later to check for changes
                tmp.SetAnnotation("daytwo.aarr.xyz/resourceVersion", cluster.Metadata.ResourceVersion);

                // set cluster name label
                tmp.SetLabel("daytwo.aarr.xyz/name", cluster.Name());
                tmp.SetLabel("daytwo.aarr.xyz/management-cluster", Environment.GetEnvironmentVariable("MANAGEMENT_CLUSTERS"));

                /*
                // copy over all labels
                foreach (var next in cluster.Labels())
                {
                    tmp.SetLabel(next.Key, next.Value);
                }
                */

                //
                Globals.service.kubeclient.CoreV1.PatchNamespacedSecret(
                    new V1Patch(tmp, V1Patch.PatchType.MergePatch), tmp.Name(), tmp.Namespace());

                //
                string _api = cluster.Spec.controlPlaneRef.kind.ToLower();
                string _group = cluster.Spec.controlPlaneRef.apiVersion.Substring(0, cluster.Spec.controlPlaneRef.apiVersion.IndexOf("/"));
                string _version = cluster.Spec.controlPlaneRef.apiVersion.Substring(cluster.Spec.controlPlaneRef.apiVersion.IndexOf("/") + 1);
                string _plural = _api + "s";

                // check if provider is already present
                ProviderK8sController? item = providers.Find(item => (item.api == _api) && (item.group == _group) && (item.version == _version) && (item.plural == _plural));
                if (item == null)
                {
                    // if not, start monitoring
                    ProviderK8sController provider = new ProviderK8sController(
                            _api, _group, _version, _plural);

                    // add to list of providers we are monitoring
                    providers.Add(provider);

                    // start listening
                    provider.Listen(Environment.GetEnvironmentVariable("MANAGEMENT_CLUSTERS"));
                }
                /*
                // copy over labels by provider
                string provider_api = cluster.Spec.controlPlaneRef.kind.ToLower();
                string provider_group = cluster.Spec.controlPlaneRef.apiVersion.Substring(0, cluster.Spec.controlPlaneRef.apiVersion.IndexOf("/"));
                string provider_version = cluster.Spec.controlPlaneRef.apiVersion.Substring(cluster.Spec.controlPlaneRef.apiVersion.IndexOf("/") + 1);
                string provider_plural = provider_api + "s";
                GenericClient provider = new GenericClient(kubeclient, provider_group, provider_version, provider_plural);

                Console.WriteLine($"provider_api: {provider_api}");
                Console.WriteLine($"provider_group: {provider_group}");
                Console.WriteLine($"provider_version: {provider_version}");
                Console.WriteLine($"provider_plural: {provider_plural}");

                // create provider class instance on the fly
                CrdProviderCluster asdf = await provider.ReadNamespacedAsync<CrdProviderCluster>(cluster.Namespace(), cluster.Name(), Globals.cancellationToken);
                Console.WriteLine("provider labels:");
                foreach (var next in asdf.Labels())
                {
                    Console.WriteLine(next.Key + ": " + next.Value);
                    tmp.SetLabel(next.Key, next.Value);
                }
                */
            }
            else
            {
                Console.WriteLine("      - cluster already added to argocd");
            }


            /*
            // locate argocd cluster secret representing this cluster
            Console.WriteLine("** sync 'addons' ...");
            V1Secret? secret = GetClusterArgocdSecret(tkc.Metadata.Name);

            // add missing labels to argocd cluster secret
            Console.WriteLine("- add missing labels to argocd cluster secret:");
            foreach (var l in tkc.Metadata.Labels)
            {
                // only process labels starting with 'addons-'
                if (!l.Key.StartsWith("addons-"))
                {
                    // skip
                    continue;
                }

                // is this label already on the secret?
                bool found = false;

                // use try catch to avoid listing labels on a secret without labels
                foreach (var label in secret.Labels())
                {
                    // only process labels starting with 'addons-'
                    if (!label.Key.StartsWith("addons-"))
                    {
                        // skip
                        continue;
                    }

                    //
                    if ((l.Key == label.Key) && (l.Value == label.Value))
                    {
                        found = true;
                        break;
                    }
                }

                // if not found, add to cluster secret
                if (!found)
                {
                    Console.WriteLine("  - " + l.Key + ": " + l.Value);
                }
            }

            // remove deleted labels from argocd cluster secret
            Console.WriteLine("- remove deleted labels from argocd cluster secret:");
            foreach (var label in secret.Labels())
            {
                // only process labels starting with 'addons-'
                if (!label.Key.StartsWith("addons-"))
                {
                    // skip
                    continue;
                }

                // is this label already on the secret?
                bool found = false;

                // use try catch to avoid listing labels on a secret without labels
                foreach (var l in tkc.Metadata.Labels)
                {
                    // only process labels starting with 'addons-'
                    if (!l.Key.StartsWith("addons-"))
                    {
                        // skip
                        continue;
                    }

                    //
                    if ((l.Key == label.Key) && (l.Value == label.Value))
                    {
                        found = true;
                        break;
                    }
                }

                // if not found, add to cluster secret
                if (!found)
                {
                    Console.WriteLine("  - " + label.Key + ": " + label.Value);
                }
            }
            */

            return;
        }
        public async Task ProcessDeleted(CrdCluster cluster)
        {
            // check if we should remove this from argocd
            V1Secret tmp = GetClusterArgocdSecret(cluster.Name());
            if (tmp == null)
            {
                Console.WriteLine("argocd is not managing this cluster, no need to remove it");
                return;
            }

            // only remove from argocd if we added this cluster to argocd
            string annotation = tmp.GetAnnotation("daytwo.aarr.xyz/resourceVersion");
            if (annotation == null)
            {
                Console.WriteLine("** annotation is null **");
                Console.WriteLine("** (don't delete cluster) **");
                return;
            }
            /*
            else
            {
                Console.WriteLine("** annotation is: "+ annotation);
            }
            */

            Console.WriteLine("** argocd remove cluster ...");

            // locate server pod
            V1PodList list = await Globals.service.kubeclient.ListNamespacedPodAsync("argocd");
            V1Pod pod = null;
            foreach (var item in list.Items)
            {
                if (item.Spec.Containers[0].Name == "server")
                {
                    pod = item;

                    break;
                }
            }

            // this shouldn't happen, but could if server is not running
            if (pod == null)
            {
                Console.WriteLine("server pod not found, unable to remove cluster from argocd");
                return;
            }

            // todo get clustername used in provided kubeconfig

            var cmds = new List<string>();
            cmds.Add("sh");
            cmds.Add("-c");
            cmds.Add( $"argocd cluster rm {cluster.Name()}"
                    + $" -y"
                    + $" --server=localhost:8080"
                    + $" --plaintext"
                    + $" --insecure"
                    + $" --auth-token={Environment.GetEnvironmentVariable("ARGOCD_AUTH_TOKEN")};"
                    );
            try
            {
                Console.WriteLine("[cluster] before exec");
                int asdf = await Globals.service.kubeclient.NamespacedPodExecAsync(
                    pod.Name(), pod.Namespace(), pod.Spec.Containers[0].Name, cmds, false, One, Globals.cancellationToken);
                Console.WriteLine("[cluster] after exec");
            }
            catch
            {
            }
            Console.WriteLine("[cluster] after exec (2)");
        }

        public static string Base64Encode(string text)
        {
            var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            return System.Convert.ToBase64String(textBytes);
        }
        public static string Base64Decode(string base64)
        {
            var base64Bytes = System.Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(base64Bytes);
        }

        public static V1Secret? GetClusterArgocdSecret(string clusterName)
        {
            //Console.WriteLine("- GetClusterSecret, clusterName: "+ clusterName);
            V1SecretList secrets = Globals.service.kubeclient.ListNamespacedSecret("argocd");

            //Console.WriteLine("- argocd cluster secrets:");
            foreach (V1Secret secret in secrets)
            {
                // is there a label indicating this is a cluster secret?
                if (secret.Labels() == null)
                {
                    //Console.WriteLine("  - skipping, a");
                    continue;
                }
                if (!secret.Labels().TryGetValue("argocd.argoproj.io/secret-type", out var value))
                {
                    //Console.WriteLine("  - skipping, b");
                    continue;
                }
                if (value != "cluster")
                {
                    //Console.WriteLine("  - skipping, c, value: "+ value);
                    continue;
                }

                // is this the cluster we are looking for?
                string name = Encoding.UTF8.GetString(secret.Data["name"], 0, secret.Data["name"].Length);
                //Console.WriteLine("  - name: " + name +", tkcName: "+ tkc.Metadata.Name);
                if (name != clusterName)
                {
                    //Console.WriteLine("  - skipping, d");
                    continue;
                }

                /*
                // use regex to match cluster name via argocd secret which represents cluster
                if (!Regex.Match(next.Name(), "cluster-" + tkc.Metadata.Name + "-" + "\\d").Success)
                {
                    // skip secret, this is not the cluster secret
                    continue;
                }
                */

                // secret located
                //Console.WriteLine("- secret located: " + secret.Name());
                return secret;
            }

            return null;
        }

        public static KubernetesClientConfiguration BuildConfigFromArgocdSecret(V1Secret secret)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();

            // form a kubeconfig via the argocd secret
            Console.WriteLine("- form kubeconfig from argocd cluster secret ...");

            // we have a cluster secret, check its name/server
            data.Add("name", Encoding.UTF8.GetString(secret.Data["name"], 0, secret.Data["name"].Length));
            data.Add("server", Encoding.UTF8.GetString(secret.Data["server"], 0, secret.Data["server"].Length));
            data.Add("config", Encoding.UTF8.GetString(secret.Data["config"], 0, secret.Data["config"].Length));

            Console.WriteLine("  -   name: " + data["name"]);
            Console.WriteLine("  - server: " + data["server"]);

            // parse kubeconfig json data from argocd secret
            JsonElement o = JsonSerializer.Deserialize<JsonElement>(data["config"]);

            // start with an empty kubeconfig
            KubernetesClientConfiguration kubeconfig = new KubernetesClientConfiguration();

            // form kubeconfig using values from argocd secret
            kubeconfig.Host = data["server"];
            kubeconfig.SkipTlsVerify = o.GetProperty("tlsClientConfig").GetProperty("insecure").GetBoolean();
            kubeconfig.ClientCertificateData = o.GetProperty("tlsClientConfig").GetProperty("certData").GetString();
            kubeconfig.ClientCertificateKeyData = o.GetProperty("tlsClientConfig").GetProperty("keyData").GetString();
            // convert caData into an x509 cert & add
            kubeconfig.SslCaCerts = new X509Certificate2Collection();
            kubeconfig.SslCaCerts.Add(
                    X509Certificate2.CreateFromPem(
                        Base64Decode(o.GetProperty("tlsClientConfig").GetProperty("caData").GetString()).AsSpan()
                ));

            return kubeconfig;
        }

        /// <summary>
        /// With knowledge of the innerworkings of the cluster provisioning process,
        /// obtain the default admin kubeconfig '/etc/kubernetes/admin.conf'.
        /// </summary>
        /// <param name="clusterName"></param>
        /// <returns></returns>
        public static async Task<KubernetesClientConfiguration> GetClusterKubeConfig(string clusterName, string clusterNamespace)
        {
            // clusterctl - n vc - test get kubeconfig vc - test
            // k -n vc-test get secrets vc-test-kubeconfig -o jsonpath='{.data.value}' | base64 -d
            Console.WriteLine($"[cluster] GetClusterKubeConfig ({clusterName}, {clusterNamespace})");

            V1Secret secret = null;
            try
            {
                secret = Globals.service.kubeclient.ReadNamespacedSecret(clusterName + "-kubeconfig", clusterNamespace);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
            secret.Data.TryGetValue("value", out byte[] bytes);
            string kubeconfig = System.Text.Encoding.UTF8.GetString(bytes);
            //Console.WriteLine("[cluster] kubeconfig:\n" + kubeconfig);

            // save kubeconfig to a temporary file
            //string path = Path.GetTempFileName();
            //string path = "/tmp/asdf.txt";
            //Console.WriteLine("tmp path: " + path);

            // exec into server pod, see if we can use 'argocd' there
            var cmds = new List<string>();

            // todo get actual pod name of 'server' pod 

            // todo get clustername used in provided kubeconfig


            // locate server pod
            V1PodList list = await Globals.service.kubeclient.ListNamespacedPodAsync("argocd");
            V1Pod pod = null;
            foreach (var item in list.Items)
            {
                if (item.Spec.Containers[0].Name == "server")
                {
                    pod = item;
                    
                    break;
                }
            }

            // this shouldn't happen, but could if server is not running
            if (pod == null)
            {
                Console.WriteLine("server pod not found");
                return null;
            }

            /*
            // test
            try
            {
                cmds = new List<string>();
                cmds.Add("pwd");
                //cmds = new List<string>();
                //cmds.Add("pwd");
                Console.WriteLine("[cluster] (test) before exec");
                await Globals.service.kubeclient.NamespacedPodExecAsync(
                    pod.Name(), pod.Namespace(), pod.Spec.Containers[0].Name, cmds, false, One, Globals.cancellationToken).ConfigureAwait(false);
                Console.WriteLine("[cluster] (test) after exec");

                //await ExecInPod(Globals.service.kubeclient, pod, "pwd");
            }
            catch (Exception ex)
            {
                Console.WriteLine("exception caught when performing 'exec', cmd ran though, ignoring exception for now");
                //Console.WriteLine(ex.ToString());
            }
            */

            try
            {
                cmds = new List<string>();
                cmds.Add("sh");
                cmds.Add("-c");
                cmds.Add($"echo {Convert.ToBase64String(bytes)} > /tmp/{clusterName}.b64;"
                        + $"cat /tmp/{clusterName}.b64 | base64 -d > /tmp/{clusterName}.conf;"
                        + $"argocd cluster add my-vcluster"
                        + $" -y"
                        + $" --upsert"
                        + $" --name {clusterName}"
                        + $" --kubeconfig /tmp/{clusterName}.conf"
                        + $" --server=localhost:8080"
                        + $" --plaintext"
                        + $" --insecure"
                        + $" --auth-token={Environment.GetEnvironmentVariable("ARGOCD_AUTH_TOKEN")};"
                        );
                Console.WriteLine("[cluster] before exec");
                int asdf = await Globals.service.kubeclient.NamespacedPodExecAsync(
                    pod.Name(), pod.Namespace(), pod.Spec.Containers[0].Name, cmds, false, One, Globals.cancellationToken).ConfigureAwait(false);
                Console.WriteLine("[cluster] after exec");
            }
            catch (Exception ex)
            {
                //Console.WriteLine("exception caught when performing 'exec', cmd ran though, ignoring exception for now");
                //Console.WriteLine(ex.ToString());
            }
            Console.WriteLine("[cluster] after exec (2)");


            return null;
        }

        public static void PrintEvenNumbers()
        {
            //Console.WriteLine("all is done");
        }
        public static Task One(Stream stdIn, Stream stdOut, Stream stdErr)
        {
            StreamReader sr = new StreamReader(stdOut);
            while (!sr.EndOfStream)
            {
                Console.WriteLine(sr.ReadLine());
            }

            // returning null will cause an exception, but it also let's us return back to the processing
            return null;
            //return new Task(PrintEvenNumbers);
        }

        private static async Task ExecInPod(IKubernetes client, V1Pod pod, string cmd)
        {
            var webSocket =
                await client.WebSocketNamespacedPodExecAsync(pod.Metadata.Name, "default", cmd,
                    pod.Spec.Containers[0].Name).ConfigureAwait(false);

            var demux = new StreamDemuxer(webSocket);
            demux.Start();

            var buff = new byte[4096];
            var stream = demux.GetStream(1, 1);
            stream.Read(buff, 0, 4096);
            var str = Encoding.Default.GetString(buff);
            Console.WriteLine(str);

            //return new Task(PrintEvenNumbers);
        }
    }
}
