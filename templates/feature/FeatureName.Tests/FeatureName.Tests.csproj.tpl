<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net11.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <AssemblyName>Pole.Features.FeatureName.Tests</AssemblyName>
        <RootNamespace>Pole.Features.FeatureName.Tests</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk"/>
        <PackageReference Include="xunit"/>
        <PackageReference Include="xunit.runner.visualstudio" PrivateAssets="all"/>
        <ProjectReference Include="../FeatureName.Domain/FeatureName.Domain.csproj"/>
        <ProjectReference Include="../FeatureName.Events/FeatureName.Events.csproj"/>
    </ItemGroup>
</Project>
