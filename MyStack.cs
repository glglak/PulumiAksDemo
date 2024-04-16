using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pulumi;
using Pulumi.Azure.ArcKubernetes;
using Pulumi.Azure.Cdn;
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
using Resource = Pulumi.Resource;
using ResourceIdentityType = Pulumi.AzureNative.ContainerService.ResourceIdentityType;
using ResourceReferenceArgs = Pulumi.AzureNative.ContainerService.Inputs.ResourceReferenceArgs;
using Route = Pulumi.AzureNative.Cdn.V20230701Preview.Route;
using RouteArgs = Pulumi.AzureNative.Cdn.V20230701Preview.RouteArgs;
using SkuArgs = Pulumi.AzureNative.Cdn.Inputs.SkuArgs;

class MyStack : Stack
{

    public Output<string>[] ServiceExternalIps { get; private set; }

    public MyStack()
    {
        var resourceGroup = new ResourceGroup("aksResourceGroup");
        var pulumiConfig = new Pulumi.Config();
        int numberOfClusters = pulumiConfig.GetInt32("clusterCount") ?? 2; // default to 2 if not specified
        ServiceExternalIps = new Output<string>[numberOfClusters];

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
        },new CustomResourceOptions
        {
            DeleteBeforeReplace=true,
             
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
        var route = new Route("defaultRoute1", new RouteArgs
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
        }, new CustomResourceOptions
        {
            DependsOn = new List<Resource> { originGroup },
            DeleteBeforeReplace = true

        });

        // Create a default route for the origin group

        for (int clusterCount = 0; clusterCount < numberOfClusters; clusterCount++)
        {
            int localClusterCount = clusterCount; // Local copy for closure capture

            var clusterOutput = CreateAKSCluster(resourceGroup, pulumiConfig, clusterCount);

            var resourceGroupOutput = Output.Create(resourceGroup.Name);
            var clusterNameOutput = clusterOutput.Apply(cluster => cluster.Name);

            // Combine resource group and cluster name outputs to fetch kubeconfig
            var kubeconfig = Output.Tuple(resourceGroupOutput, clusterNameOutput).Apply(names =>
                ListManagedClusterUserCredentials.Invoke(new ListManagedClusterUserCredentialsInvokeArgs
                {
                    ResourceGroupName = names.Item1,
                    ResourceName = names.Item2,
                }).Apply(creds =>
                {
                    var encodedKubeconfig = creds.Kubeconfigs[0].Value;
                    return Encoding.UTF8.GetString(Convert.FromBase64String(encodedKubeconfig));
                })
            );

            // Ensure Kubernetes provider initialization after kubeconfig is fully ready
            var k8sProviderOutput = kubeconfig.Apply(decodedKubeconfig =>
                new Pulumi.Kubernetes.Provider("k8sprovider" + localClusterCount.ToString(), new Pulumi.Kubernetes.ProviderArgs
                {
                    KubeConfig = decodedKubeconfig
                })
            );

            // Apply the Kubernetes YAML deployment after the provider is ready
            var deploymentName = $"aksAppDemoDeployment-{localClusterCount}";
            var appDeployment = k8sProviderOutput.Apply(provider =>
                new Pulumi.Kubernetes.Yaml.ConfigFile(deploymentName,
                    new Pulumi.Kubernetes.Yaml.ConfigFileArgs
                    {
                        File = (localClusterCount == 0) ? pulumiConfig.Get("testAppEastUs") : pulumiConfig.Get("testAppWestEurope")
                    }, new ComponentResourceOptions { Provider = provider }
                )
            );

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
                  { "app", serviceName }
            },
                    Ports = new InputList<Pulumi.Kubernetes.Types.Inputs.Core.V1.ServicePortArgs>
            {
                   new Pulumi.Kubernetes.Types.Inputs.Core.V1.ServicePortArgs
            {
                Port = 80,
                TargetPort = 80  // Setting the TargetPort as an integer
            }
                },

                    Type = "LoadBalancer"
                }
            });

            // Capture the service's LoadBalancer IP.
            this.ServiceExternalIps[clusterCount] = serviceResource.Status.Apply(status => status.LoadBalancer.Ingress[0].Ip);
            Output.Format($"Service IP for cluster {clusterCount} is {this.ServiceExternalIps[clusterCount]}");



            var origin = new AFDOrigin("origin" + clusterCount, new Pulumi.AzureNative.Cdn.V20230701Preview.AFDOriginArgs
            {
                ResourceGroupName = resourceGroup.Name,
                ProfileName = profile.Name,
                OriginGroupName = originGroup.Name,
                OriginHostHeader = this.ServiceExternalIps[clusterCount], // Now correctly using the resolved IP
                HostName = this.ServiceExternalIps[clusterCount], // Now correctly using the resolved IP
                HttpPort = 80,
                HttpsPort = 443,
                EnabledState = "Enabled",
                Priority = 1,
                Weight = 500,
                EnforceCertificateNameCheck = false
            });


        }

    }

    private static Output<ManagedCluster> CreateAKSCluster(ResourceGroup resourceGroup, Config pulumiConfig, int clusterCount)
    {
        return   Output.Create(new ManagedCluster(pulumiConfig.Get("clustername") + clusterCount ?? "AksCluster" + clusterCount,
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
            }));
    }

    





}
