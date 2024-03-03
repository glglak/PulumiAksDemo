using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pulumi;
using Pulumi.Azure.FrontDoor;
using Pulumi.Azure.FrontDoor.Inputs;
using Pulumi.AzureNative.Cdn.V20230701Preview.Inputs;
using Pulumi.AzureNative.ContainerService;
using Pulumi.AzureNative.ContainerService.Inputs;
using Pulumi.AzureNative.Resources;
using AFDEndpoint = Pulumi.AzureNative.Cdn.V20230701Preview.AFDEndpoint;
using AFDOrigin = Pulumi.AzureNative.Cdn.V20230701Preview.AFDOrigin;
using AFDOriginGroup = Pulumi.AzureNative.Cdn.V20230701Preview.AFDOriginGroup;
using AFDOriginGroupArgs = Pulumi.AzureNative.Cdn.V20230701Preview.AFDOriginGroupArgs;
using ForwardingProtocol = Pulumi.AzureNative.Cdn.V20230701Preview.ForwardingProtocol;
using HealthProbeRequestType = Pulumi.AzureNative.Cdn.V20230701Preview.HealthProbeRequestType;
using ProbeProtocol = Pulumi.AzureNative.Cdn.V20230701Preview.ProbeProtocol;
using Profile = Pulumi.AzureNative.Cdn.V20230701Preview.Profile;
using ProfileArgs = Pulumi.AzureNative.Cdn.V20230701Preview.ProfileArgs;
using ResourceIdentityType = Pulumi.AzureNative.ContainerService.ResourceIdentityType;
using ResourceReferenceArgs = Pulumi.AzureNative.ContainerService.Inputs.ResourceReferenceArgs;
using Route = Pulumi.AzureNative.Cdn.V20230701Preview.Route;
using RouteArgs = Pulumi.AzureNative.Cdn.V20230701Preview.RouteArgs;
using SkuArgs = Pulumi.AzureNative.Cdn.Inputs.SkuArgs;

class MyStack : Stack
{
    [Output]
    public Output<string> KubeConfig { get; set; }

    [Output]
    public Output<string> ServiceExternalIp { get; set; }

    public MyStack()
    {
        var resourceGroup = new ResourceGroup("aksResourceGroup");
        var pulumiConfig = new Pulumi.Config();
        // Create an Azure Front Door profile
        var profile = new Profile("myFrontDoorProfile", new ProfileArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Location = "Global", // Front Door service is always "Global"
            Sku = new Pulumi.AzureNative.Cdn.V20230701Preview.Inputs.SkuArgs
            {
                Name = "Standard_AzureFrontDoor"
            }
        });
        var originGroup = new AFDOriginGroup("myOriginGroup", new AFDOriginGroupArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ProfileName = profile.Name,
            LoadBalancingSettings = new Pulumi.AzureNative.Cdn.V20230701Preview.Inputs.LoadBalancingSettingsParametersArgs
            {
                SampleSize = 4,
                SuccessfulSamplesRequired = 2
            },
            HealthProbeSettings = new HealthProbeParametersArgs
            {
                ProbePath = "/health",
                ProbeProtocol = ProbeProtocol.Https,
                ProbeRequestType = HealthProbeRequestType.GET,
                ProbeIntervalInSeconds = 60
            }
        });
        // Create a Frontend Endpoint
        var frontendEndpoint = new AFDEndpoint("myFrontendEndpoint", new Pulumi.AzureNative.Cdn.V20230701Preview.AFDEndpointArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ProfileName = profile.Name,
            EnabledState = "Enabled",
            Location = pulumiConfig.Get("cluster2location")
            // Specify other properties as required
        });
        // Create a default route for the origin group
        var route = new Route("defaultRoute", new RouteArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ProfileName = profile.Name,
            EndpointName = frontendEndpoint.Name,
            OriginGroup = new Pulumi.AzureNative.Cdn.V20230701Preview.Inputs.ResourceReferenceArgs
            {
                Id = originGroup.Id
            },
            PatternsToMatch = new List<string> { "/*" },
            ForwardingProtocol = ForwardingProtocol.HttpOnly,
            EnabledState = "Enabled",
            LinkToDefaultDomain = "Enabled"
            // Specify other properties as required
        });
        for (int clusterCount = 0; clusterCount < (pulumiConfig.GetInt32("clusterCount") ?? 2); clusterCount++)
        {
            ManagedCluster cluster = CreateAKSCluster(resourceGroup, pulumiConfig, clusterCount);

            this.ServiceExternalIp = DeployAppIntoAKS(resourceGroup, pulumiConfig, clusterCount, cluster);


            var origin = new AFDOrigin("origin" + clusterCount, new Pulumi.AzureNative.Cdn.V20230701Preview.AFDOriginArgs
            {
                ResourceGroupName = resourceGroup.Name,
                ProfileName = profile.Name,
                OriginGroupName = originGroup.Name,
                OriginHostHeader = ServiceExternalIp,
                HostName = ServiceExternalIp,
                HttpPort = 80,
                HttpsPort = 443,
                EnabledState = "Enabled",
                Priority = 1,
                Weight = 500,
                EnforceCertificateNameCheck = false

            });



        }


    }

    private static ManagedCluster CreateAKSCluster(ResourceGroup resourceGroup, Config pulumiConfig, int clusterCount)
    {
        return new ManagedCluster(pulumiConfig.Get("clustername") + clusterCount ?? "AksCluster" + clusterCount,
            new ManagedClusterArgs
            {
                ResourceGroupName = resourceGroup.Name,
                AgentPoolProfiles = new ManagedClusterAgentPoolProfileArgs
                {
                    Count = pulumiConfig.GetInt32("nodecount") ?? 3,
                    Mode = "System",
                    Name = "agentpool",
                    OsDiskSizeGB = 30,
                    OsType = pulumiConfig.Get("ostype") ?? "Linux",
                    VmSize = pulumiConfig.Get("vmsize") ?? "Standard_DS2_v2"
                },
                DnsPrefix = pulumiConfig.Get("clustername") + clusterCount,
                Identity = new ManagedClusterIdentityArgs
                {
                    Type = ResourceIdentityType.SystemAssigned,
                },
                Location = clusterCount == 0 ? (pulumiConfig.Get("clusterLocation1") ?? "East US") : (pulumiConfig.Get("clusterLocation2") ?? "WestEurope")
            });
    }

    private static Output<string> DeployAppIntoAKS(ResourceGroup resourceGroup, Config pulumiConfig, int clusterCount, ManagedCluster cluster)
    {
        var kubeconfig = Output.Tuple(resourceGroup.Name, cluster.Name).Apply(names =>
        ListManagedClusterUserCredentials.Invoke(new ListManagedClusterUserCredentialsInvokeArgs
        {
            ResourceGroupName = names.Item1,
            ResourceName = names.Item2,
        })).Apply(creds =>
        {
            var encodedKubeconfig = creds.Kubeconfigs[0].Value;
            var decodedKubeconfig = Encoding.UTF8.GetString(Convert.FromBase64String(encodedKubeconfig));
            return decodedKubeconfig;
        });
        var k8sProvider = new Pulumi.Kubernetes.Provider("k8sprovider" + clusterCount.ToString(), new Pulumi.Kubernetes.ProviderArgs
        {

            KubeConfig = kubeconfig
        });

        // Apply the Kubernetes YAML deployment to the Kubernetes cluster.
        var appDeployment = new Pulumi.Kubernetes.Yaml.ConfigFile("aksAppDemoDeployment" + clusterCount.ToString(),
            new Pulumi.Kubernetes.Yaml.ConfigFileArgs
            {
                File = (clusterCount == 0) ? pulumiConfig.Get("testAppEastUs") : pulumiConfig.Get("testAppWestEurope"),
            }, new ComponentResourceOptions { Provider = k8sProvider });
        var serviceName = (clusterCount == 0) ? pulumiConfig.Get("testAppNameEastUs") : pulumiConfig.Get("testAppNameWestUs"); // Replace with your actual service name.

        var serviceNamespace = "default"; // Replace with the namespace if not default.

        var serviceResource = new Pulumi.Kubernetes.Core.V1.Service(serviceName, new Pulumi.Kubernetes.Types.Inputs.Core.V1.ServiceArgs
        {
            Metadata = new Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
            {
                Name = serviceName,
                Namespace = serviceNamespace,

            },
            Spec = new Pulumi.Kubernetes.Types.Inputs.Core.V1.ServiceSpecArgs
            {
                Selector = new InputMap<string>
            {
            {
                        "app", serviceName
                    } // Make sure to replace "YourAppLabel" with the actual label that your Pods have.
            },
                Ports = new InputList<Pulumi.Kubernetes.Types.Inputs.Core.V1.ServicePortArgs>
            {
          
            },

                Type = "LoadBalancer"
            }
        }, new CustomResourceOptions { Provider = k8sProvider });

        // Capture the service's LoadBalancer IP.
        var serviceIP = serviceResource.Status.Apply(status => status.LoadBalancer.Ingress[0].Ip);
        return serviceIP;

    }





}
