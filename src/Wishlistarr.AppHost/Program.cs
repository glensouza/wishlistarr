IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.Wishlistarr_Api>("api", "http");

builder.AddViteApp("web", "../../web")
    .WithEnvironment("WISHLISTARR_API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
