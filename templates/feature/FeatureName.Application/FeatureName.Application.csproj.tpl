<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Pole.Features.FeatureName.Application</AssemblyName>
    <RootNamespace>Pole.Features.FeatureName.Application</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="FluentValidation" />
    <ProjectReference Include="../FeatureName.Domain/FeatureName.Domain.csproj" />
    <ProjectReference Include="../FeatureName.Events/FeatureName.Events.csproj" />
    <ProjectReference Include="../../../shared/Database/Database.csproj" />
    <ProjectReference Include="../../../shared/Mediator/Mediator.csproj" />
    <ProjectReference Include="../../../shared/helpers/ASP/ASP.csproj" />
    <ProjectReference Include="../../../shared/Validation/Validation.csproj" />
    <ProjectReference Include="../../Outbox/Outbox.Contracts/Outbox.Contracts.csproj" />
  </ItemGroup>
</Project>
