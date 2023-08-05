﻿using daytwo;
using k8s.Models;
using k8s;
using System.Text.Json;
using daytwo.CustomResourceDefinitions;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Buffers.Text;
using Microsoft.AspNetCore.DataProtection;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;
using k8s.KubeConfigModels;
using System.Runtime.CompilerServices;
using YamlDotNet.Serialization.NamingConventions;
using System.Reflection.Emit;
using Json.Patch;
using daytwo.Helpers;
using daytwo.crd.provider;
using System.Threading;

namespace daytwo.K8sControllers
{
    /*
    public class Provider : IComparable<Provider>
    {
        string api;
        string group;
        string version;
        string plural;

        public GenericClient generic = null;// new GenericClient(Globals.service.kubeclient, group, version, plural);
        public Kubernetes kubeclient = null;
        //public KubernetesClientConfiguration kubeconfig = null;

        public Provider(Kubernetes kubeclient, string api, string group, string version, string plural)
        {
            generic = new GenericClient(kubeclient, group, version, plural);
        }

        public int CompareTo(Provider? other)
        {
            throw new NotImplementedException();
        }
    }
    */
    public class ProviderK8sController
    {
        public string api; // = "tanzukubernetescluster";
        public string group; // = "run.tanzu.vmware.com";
        public string version; // = "v1alpha2";
        public string plural; // = api + "s";

        public string managementCluster;

        public Kubernetes kubeclient = null;
        public KubernetesClientConfiguration kubeconfig = null;

        public GenericClient generic = null;

        // Enforce only processing one watch event at a time
        SemaphoreSlim semaphore = null;


        public ProviderK8sController(string managementCluster, string api, string group, string version, string plural)
        {
            // initialize properties
            this.api = api;
            this.group = group;
            this.version = version;
            this.plural = plural;

            // remember which management cluster is using this provider type
            this.managementCluster = managementCluster;

            // locate the provisioning cluster argocd secret
            V1Secret? secret = daytwo.Helpers.Main.GetClusterArgocdSecret(managementCluster);
            // use secret to create kubeconfig
            kubeconfig = daytwo.Helpers.Main.BuildConfigFromArgocdSecret(secret);
            // use kubeconfig to create client
            kubeclient = new Kubernetes(kubeconfig);

            //
            generic = new GenericClient(kubeclient, group, version, plural);

            // Prep semaphore for only 1 action at a time
            semaphore = new SemaphoreSlim(1);

            // start listening
            Globals.log.LogInformation($"**** Provider.Add({api}s.{group}.{version})");
        }
        public async Task Intermittent(int seconds)
        {
            while (true)
            {
                // intermittent delay in between checks
                Globals.log.LogInformation("sleeping");
                Thread.Sleep(seconds * 1000);

                // Acquire Semaphore
                semaphore.Wait(Globals.cancellationToken);

                try
                {
                    //**
                    // add: sync labels from provider resource to argocd cluster secret

                    // acquire list of all provider resources
                    CustomResourceList<CrdProviderCluster> list = await generic.ListNamespacedAsync<CustomResourceList<CrdProviderCluster>>("");
                    foreach (var item in list.Items)
                    {
                        /*
                        // check that this secret is an argocd cluster secret
                        if (item.Labels() == null)
                        {
                            //Globals.log.LogInformation("- ignoring, not a cluster secret");
                            continue;
                        }
                        if (!item.Labels().TryGetValue("argocd.argoproj.io/secret-type", out var value))
                        {
                            //Globals.log.LogInformation("- ignoring, not a cluster secret");
                            continue;
                        }
                        if (value != "cluster")
                        {
                            //Globals.log.LogInformation("- ignoring, not a cluster secret");
                            continue;
                        }
                        */

                        // sync labels
                        await ProcessAdded(item);
                    }

                    //**
                    // remove: nothing to do here
                }
                catch (Exception ex)
                {
                    Globals.log.LogInformation($"{ex.Message}", ex);
                }

                try
                {
                    // Release semaphore
                    semaphore.Release();
                }
                catch
                {
                    // release will fail if exception was before semaphore was acquired, ignore
                }
            }
        }

        public async Task Listen()
        {
            // Watch is a tcp connection therefore it can drop, use a while loop to restart as needed.
            while (true)
            {
                Globals.log.LogInformation(new EventId(1, api), "(" + api +") Listen begins ...");
                try
                {
                    await foreach (var (type, item) in generic.WatchNamespacedAsync<CrdProviderCluster>(""))
                    {
                        Globals.log.LogInformation(new EventId(1, api), "");
                        Globals.log.LogInformation(new EventId(1, api), "(event) [" + type + "] " + plural + "." + group + "/" + version + ": " + item.Metadata.Name);

                        // Acquire Semaphore
                        semaphore.Wait(Globals.cancellationToken);
                        //Globals.log.LogInformation(new EventId(1, api), "[" + item.Metadata.Name + "]");

                        // Handle event type
                        switch (type)
                        {
                            //case WatchEventType.Added:
                            //    await ProcessAdded(item);
                            //    break;
                            //case WatchEventType.Bookmark:
                            //    break;
                            //case WatchEventType.Deleted:
                            //    await ProcessDeleted(item);
                            //    break;
                            //case WatchEventType.Error:
                            //    break;
                            case WatchEventType.Modified:
                                await ProcessModified(item);
                                break;
                        }

                        // Release semaphore
                        semaphore.Release();
                    }
                }
                catch (k8s.Autorest.HttpOperationException ex)
                {
                    Globals.log.LogInformation(new EventId(1, api), "Exception: " + ex);
                    switch (ex.Response.StatusCode)
                    {
                        // crd is missing, sleep to avoid an error loop
                        case System.Net.HttpStatusCode.NotFound:
                            Globals.log.LogInformation(new EventId(1, api), "listen recieved: 404, is clusters.cluster.x-k8s.io crd is missing?");
                            Thread.Sleep(1000);
                            break;
                    }

                    try
                    {
                        // Release semaphore
                        semaphore.Release();
                    }
                    catch
                    {
                        // release will fail if exception was before semaphore was acquired, ignore
                    }
                }
                catch (Exception ex)
                {
                    //Globals.log.LogInformation(new EventId(1, api), "Exception occured while performing 'watch': " + ex);

                    try
                    {
                        // Release semaphore
                        semaphore.Release();
                    }
                    catch
                    {
                        // release will fail if exception was before semaphore was acquired, ignore
                    }
                }
            }
        }

        public async Task ProcessAdded(CrdProviderCluster provider)
        {
            await ProcessModified(provider);
        }
        public async Task ProcessModified(CrdProviderCluster provider)
        {
            Dictionary<string, string> data = new Dictionary<string, string>();
            string patchStr = string.Empty;

            Globals.log.LogInformation(new EventId(1, api), "Addition/Modify detected: " + provider.Metadata.Name);

            // acquire argocd cluster secret to so we can sync labels
            V1Secret? secret = daytwo.Helpers.Main.GetClusterArgocdSecret(provider.Name(), managementCluster);
            if (secret == null)
            {
                Globals.log.LogInformation(new EventId(1, api), "(vcluster) unable to locate argocd secret");
                return;
            }

            // check if daytwo is managing this secret
            if (secret.Metadata.EnsureAnnotations()["daytwo.aarr.xyz/resourceVersion"] == null)
            {
                Globals.log.LogInformation(new EventId(1, api), "(vcluster) secret is not managed by daytwo, ignoring");
                return;
            }

            // locate argocd cluster secret representing this cluster
            Globals.log.LogInformation(new EventId(1, api), "** sync 'addons' ...");

            //
            var before = JsonSerializer.SerializeToDocument(secret);
            bool isChange = false;

            // add missing labels to argocd cluster secret
            Globals.log.LogInformation(new EventId(1, api), "- add missing labels to argocd cluster secret:");
            foreach (var l in provider.Metadata.Labels)
            {
                /*
                // only process labels starting with 'addons-'
                if (!l.Key.StartsWith("addons-"))
                {
                    // skip
                    continue;
                }
                */

                // is this label already on the secret?
                bool found = false;

                // use try catch to avoid listing labels on a secret without labels
                foreach (var label in secret.Labels())
                {
                    /*
                    // only process labels starting with 'addons-'
                    if (!label.Key.StartsWith("addons-"))
                    {
                        // skip
                        continue;
                    }
                    */

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
                    Globals.log.LogInformation(new EventId(1, api), "  - " + l.Key + ": " + l.Value);
                    secret.SetLabel(l.Key, l.Value);
                    isChange = true;
                }
            }

            // remove deleted labels from argocd cluster secret
            Globals.log.LogInformation(new EventId(1, api), "- remove deleted labels from argocd cluster secret:");
            foreach (var label in secret.Labels())
            {
                // avoid deleting argocd labels
                if (label.Key.StartsWith("argocd.argoproj.io/"))
                {
                    continue;
                }

                /*
                // only process labels starting with 'addons-'
                if (!label.Key.StartsWith("addons-"))
                {
                    // skip
                    continue;
                }
                */

                // is this label already on the secret?
                bool found = false;

                // use try catch to avoid listing labels on a secret without labels
                foreach (var l in provider.Metadata.Labels)
                {
                    /*
                    // only process labels starting with 'addons-'
                    if (!l.Key.StartsWith("addons-"))
                    {
                        // skip
                        continue;
                    }
                    */

                    //
                    if ((l.Key == label.Key) && (l.Value == label.Value))
                    {
                        found = true;
                        break;
                    }
                }

                // if not found, remove to cluster secret
                if (!found)
                {
                    Globals.log.LogInformation(new EventId(1, api), "  - " + label.Key + ": " + label.Value);
                    secret.SetLabel(label.Key, null);
                    isChange = true;
                }
            }

            // if no changes then return now without patching
            if (!isChange)
            {
                Globals.log.LogInformation(new EventId(1, api), "- no changes detected, update complete");
                return;
            }

            // display adjusted label list
            Globals.log.LogInformation(new EventId(1, api), "resulting label list:");
            foreach (var next in secret.Labels())
            {
                Globals.log.LogInformation(new EventId(1, api), "- "+ next.Key +": "+ next.Value);
            }

            // generate json patch
            var patch = before.CreatePatch(secret);

            /*
            var patch = new JsonPatchDocument<V1Secret>();
            patch.Replace(x => x.Metadata.Labels, secret.Labels());
            patchStr = Newtonsoft.Json.JsonConvert.SerializeObject(patch);
            patchStr = patchStr.Replace("/Metadata/Labels", "/metadata/labels");
            Globals.log.LogInformation(new EventId(1, api), "patch:");
            Globals.log.LogInformation(patchStr);
            */
            try
            {
                Globals.service.kubeclient.CoreV1.PatchNamespacedSecret(
                        new V1Patch(patch, V1Patch.PatchType.JsonPatch), secret.Name(), secret.Namespace());
                        //new V1Patch(patchStr, V1Patch.PatchType.JsonPatch), secret.Name(), secret.Namespace());
            }
            catch (Exception e) {
                Globals.log.LogInformation(e.Message);
            }

            return;
        }
        public async Task ProcessDeleted(CrdProviderCluster provider)
        {
            Globals.log.LogInformation(new EventId(1, api), "Deleted detected: " + provider.Metadata.Name);
        }
    }
}
