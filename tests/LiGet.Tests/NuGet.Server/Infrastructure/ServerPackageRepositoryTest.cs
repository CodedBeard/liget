﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using LiGet.NuGet.Server.Infrastructure;
using LiGet.Tests;
using Moq;
using NuGet;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Versioning;
using Xunit;

namespace LiGet.NuGet.Server.Tests
{
    public class ServerPackageRepositoryTest : IDisposable
    {
        ServerPackageRepositoryConfig config = new ServerPackageRepositoryConfig() {
            // defaults from original impl
            EnableDelisting = false,
            EnableFrameworkFiltering = false,
            EnableFileSystemMonitoring = true,
            IgnoreSymbolsPackages = false,
            AllowOverrideExistingPackageOnPush = true,
            RunBackgroundTasks = false
        };

        private TemporaryDirectory tmpDir;

        public ServerPackageRepositoryTest() {
            TestBootstrapper.ConfigureLogging();
            tmpDir = new TemporaryDirectory();
        }

        public void Dispose() {
            tmpDir.Dispose();
        }

        public ServerPackageRepository CreateServerPackageRepository(
            string path,
            ServerPackageRepositoryConfig config,
            Action<ExpandedPackageRepository> setupRepository = null)
        {
            config.RootPath = path;
            var expandedPackageRepository = new ExpandedPackageRepository(config);

            setupRepository?.Invoke(expandedPackageRepository);

            var serverRepository = new ServerPackageRepository(
                innerRepository: expandedPackageRepository,
                serverConfig: config);

            serverRepository.GetPackages(); // caches the files

            return serverRepository;
        }

        private ServerPackageRepository CreateServerPackageRepositoryWithSemVer2(TemporaryDirectory temporaryDirectory)
        {
            return CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
            {
                repository.AddPackage(CreatePackage("test1", "1.0"));
                repository.AddPackage(CreatePackage("test2", "1.0-beta"));
                repository.AddPackage(CreatePackage("test3", "1.0-beta.1"));
                repository.AddPackage(CreatePackage("test4", "1.0-beta+foo"));
            });
        }

        [Fact]
        public void ServerPackageRepository_CreateServerPackage()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config);
                var pkg = CreatePackage("test", "1.11", builder => {
                    builder.Description = "Description";
                    builder.Authors.Add("Test Author" );
                    //TODO assign all properties and assert
                    var mockFile = new Mock<IPackageFile>();
                    mockFile.Setup(m => m.Path).Returns("foo");
                    mockFile.Setup(m => m.GetStream()).Returns(new MemoryStream());
                    builder.Files.Add(mockFile.Object);
                });
                string hashPath = Path.ChangeExtension(pkg.Path, ".nupkg.sha512");
                File.WriteAllText(hashPath,"123");
                string nuspecPath = Path.ChangeExtension(pkg.Path, ".nuspec");
                using(var nuspecFs = File.OpenWrite(nuspecPath)) {
                    pkg.GetReader().GetNuspec().CopyTo(nuspecFs);
                }
                var srvPkg = serverRepository.CreateServerPackage(pkg,false);
                Assert.Equal("Description", srvPkg.Description);
            }
        }

        [Fact]
        public void ServerPackageRepositoryAddsPackagesFromDropFolderOnStart()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var packagesToAddToDropFolder = new Dictionary<string, LocalPackageInfo>
                {
                    {"test.1.11.nupkg", CreatePackage("test", "1.11")},
                    {"test.1.9.nupkg", CreatePackage("test", "1.9")},
                    {"test.2.0-alpha.nupkg", CreatePackage("test", "2.0-alpha")},
                    {"test.2.0.0.nupkg", CreatePackage("test", "2.0.0")},
                    {"test.2.0.0-0test.nupkg", CreatePackage("test", "2.0.0-0test")},
                    {"test.2.0.0-test+tag.nupkg", CreatePackage("test", "2.0.0-test+tag")}
                };
                foreach (var packageToAddToDropFolder in packagesToAddToDropFolder)
                {
                    string dest = Path.Combine(temporaryDirectory.Path, packageToAddToDropFolder.Key);
                    File.Copy(packageToAddToDropFolder.Value.Path, dest);
                }

                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config);

                // Act
                var packages = serverRepository.GetPackages(ClientCompatibility.Max);

                // Assert
                Assert.Equal(packagesToAddToDropFolder.Count, packages.Count());
                foreach (var packageToAddToDropFolder in packagesToAddToDropFolder)
                {
                    var package = packages.FirstOrDefault(
                            p => p.Id == packageToAddToDropFolder.Value.Identity.Id 
                                && p.Version == packageToAddToDropFolder.Value.Identity.Version);

                    // check the package from drop folder has been added
                    Assert.NotNull(package); 

                    // check the package in the drop folder has been removed
                    Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, packageToAddToDropFolder.Key)));
                }
            }
        }

        // [Fact]
        // public void ServerPackageRepositoryRemovePackage()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test", "1.11"));
        //             repository.AddPackage(CreatePackage("test", "1.9"));
        //             repository.AddPackage(CreatePackage("test", "2.0-alpha"));
        //             repository.AddPackage(CreatePackage("test", "2.0.0"));
        //             repository.AddPackage(CreatePackage("test", "2.0.0-0test"));
        //             repository.AddPackage(CreatePackage("test", "2.0.0-test+tag"));
        //             repository.AddPackage(CreatePackage("test", "2.0.1+taggedOnly"));
        //         });

        //         // Act
        //         serverRepository.RemovePackage(CreatePackageIdentity("test", "1.11"));
        //         serverRepository.RemovePackage(CreatePackageIdentity("test", "2.0-alpha"));
        //         serverRepository.RemovePackage(CreatePackageIdentity("test", "2.0.1"));
        //         serverRepository.RemovePackage(CreatePackageIdentity("test", "2.0.0-0test"));
        //         var packages = serverRepository.GetPackages(ClientCompatibility.Max);

        //         // Assert
        //         Assert.Equal(3, packages.Count());
        //         Assert.Equal(1, packages.Count(p => p.SemVer2IsLatest));
        //         Assert.Equal("2.0.0", packages.First(p => p.SemVer2IsLatest).Version.ToString());

        //         Assert.Equal(1, packages.Count(p => p.SemVer2IsAbsoluteLatest));
        //         Assert.Equal("2.0.0", packages.First(p => p.SemVer2IsAbsoluteLatest).Version.ToString());
        //     }
        // }

        private PackageIdentity CreatePackageIdentity(string id, string version)
        {
            return new PackageIdentity(id, NuGetVersion.Parse(version));
        }

        // [Fact]
        // public void ServerPackageRepositorySearch()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test", "1.0"));
        //             repository.AddPackage(CreatePackage("test2", "1.0"));
        //             repository.AddPackage(CreatePackage("test3", "1.0-alpha"));
        //             repository.AddPackage(CreatePackage("test3", "2.0.0"));
        //             repository.AddPackage(CreatePackage("test4", "2.0"));
        //             repository.AddPackage(CreatePackage("test5", "1.0.0-0test"));
        //             repository.AddPackage(CreatePackage("test6", "1.2.3+taggedOnly"));
        //         });

        //         // Act
        //         var includePrerelease = serverRepository.Search(
        //             "test3",
        //             targetFrameworks: Enumerable.Empty<string>(),
        //             allowPrereleaseVersions: true,
        //             compatibility: ClientCompatibility.Max);
        //         var excludePrerelease = serverRepository.Search(
        //             "test3",
        //             targetFrameworks: Enumerable.Empty<string>(),
        //             allowPrereleaseVersions: false,
        //             compatibility: ClientCompatibility.Max);
        //         var ignoreTag = serverRepository.Search(
        //             "test6",
        //             targetFrameworks: Enumerable.Empty<string>(),
        //             allowPrereleaseVersions: false,
        //             compatibility: ClientCompatibility.Max);

        //         // Assert
        //         Assert.Equal("test3", includePrerelease.First().Id);
        //         Assert.Equal(2, includePrerelease.Count());
        //         Assert.Equal(1, excludePrerelease.Count());
        //         Assert.Equal("test6", ignoreTag.First().Id);
        //         Assert.Equal(1, ignoreTag.Count());
        //     }
        // }

        [Fact]
        public void ServerPackageRepositorySearchSupportsFilteringOutSemVer2()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepositoryWithSemVer2(temporaryDirectory);

                // Act
                var actual = serverRepository.Search(
                    "test",
                    targetFrameworks: Enumerable.Empty<string>(),
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Default);

                // Assert
                var packages = actual.OrderBy(p => p.Id).ToList();
                Assert.Equal(2, packages.Count);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("test2", packages[1].Id);
            }
        }

        // [Fact]
        // public void ServerPackageRepositorySearchUnlisted()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         config.EnableDelisting = true;
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test1", "1.0"));
        //         });

        //         // Assert base setup
        //         var packages = serverRepository.Search("test1", true).ToList();
        //         Assert.Equal(1, packages.Count);
        //         Assert.Equal("test1", packages[0].Id);
        //         Assert.Equal("1.0", packages[0].Version.ToString());

        //         // Delist the package
        //         serverRepository.RemovePackage("test1", new SemanticVersion("1.0"));

        //         // Verify that the package is not returned by search
        //         packages = serverRepository.Search("test1", allowPrereleaseVersions: true).ToList();
        //         Assert.Equal(0, packages.Count);

        //         // Act: search with includeDelisted=true
        //         packages = serverRepository.GetPackages().ToList();

        //         // Assert
        //         Assert.Equal(1, packages.Count);
        //         Assert.Equal("test1", packages[0].Id);
        //         Assert.Equal("1.0", packages[0].Version.ToString());
        //         Assert.False(packages[0].Listed);
        //     }
        // }

        [Fact]
        public void ServerPackageRepositoryFindPackageById()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
                {
                    repository.AddPackage(CreatePackage("test", "1.0"));
                    repository.AddPackage(CreatePackage("test2", "1.0"));
                    repository.AddPackage(CreatePackage("test3", "1.0-alpha"));
                    repository.AddPackage(CreatePackage("test4", "2.0"));
                    repository.AddPackage(CreatePackage("test4", "3.0.0+tagged"));
                    repository.AddPackage(CreatePackage("Not5", "4.0"));
                });

                // Act
                var valid = serverRepository.FindPackagesById("test");
                var invalid = serverRepository.FindPackagesById("bad");

                // Assert
                Assert.Equal("test", valid.First().Id);
                Assert.Equal(0, invalid.Count());
            }
        }

        [Fact]
        public void ServerPackageRepositoryFindPackageByIdSupportsFilteringOutSemVer2()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepositoryWithSemVer2(temporaryDirectory);

                // Act
                var actual = serverRepository.FindPackagesById("test3");

                // Assert
                Assert.Empty(actual);
            }
        }

        [Fact]
        public void ServerPackageRepositoryFindPackage()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
                {
                    repository.AddPackage(CreatePackage("test", "1.0"));
                    repository.AddPackage(CreatePackage("test2", "1.0"));
                    repository.AddPackage(CreatePackage("test3", "1.0.0-alpha"));
                    repository.AddPackage(CreatePackage("test4", "2.0"));
                    repository.AddPackage(CreatePackage("test4", "3.0.0+tagged"));
                    repository.AddPackage(CreatePackage("Not5", "4.0.0"));
                });

                // Act
                var valid = serverRepository.FindPackage("test4", NuGetVersion.Parse("3.0.0"));
                var valid2 = serverRepository.FindPackage("Not5", NuGetVersion.Parse("4.0"));
                var validPreRel = serverRepository.FindPackage("test3", NuGetVersion.Parse("1.0.0-alpha"));
                var invalidPreRel = serverRepository.FindPackage("test3", NuGetVersion.Parse("1.0.0"));
                var invalid = serverRepository.FindPackage("bad", NuGetVersion.Parse("1.0"));

                // Assert
                Assert.Equal("test4", valid.Id);
                Assert.Equal("Not5", valid2.Id);
                Assert.Equal("test3", validPreRel.Id);
                Assert.Null(invalidPreRel);
                Assert.Null(invalid);
            }
        }

        //TODO latest caching
        // [Fact]
        // public void ServerPackageRepositoryMultipleIds()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test", "0.9"));
        //             repository.AddPackage(CreatePackage("test", "1.0"));
        //             repository.AddPackage(CreatePackage("test2", "1.0"));
        //             repository.AddPackage(CreatePackage("test3", "1.0-alpha"));
        //             repository.AddPackage(CreatePackage("test3", "2.0.0+taggedOnly"));
        //             repository.AddPackage(CreatePackage("test4", "2.0"));
        //             repository.AddPackage(CreatePackage("test4", "3.0.0"));
        //             repository.AddPackage(CreatePackage("test5", "2.0.0-onlyPre+tagged"));
        //         });

        //         // Act
        //         var packages = serverRepository.GetPackages(ClientCompatibility.Max);

        //         // Assert
        //         Assert.Equal(5, packages.Count(p => p.SemVer2IsAbsoluteLatest));
        //         Assert.Equal(4, packages.Count(p => p.SemVer2IsLatest));
        //         Assert.Equal(3, packages.Count(p => !p.SemVer2IsAbsoluteLatest));
        //         Assert.Equal(4, packages.Count(p => !p.SemVer2IsLatest));
        //     }
        // }

        [Fact]
        public void ServerPackageRepositorySemVer1IsAbsoluteLatest()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
                {
                    repository.AddPackage(CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.2-beta"));
                    repository.AddPackage(CreatePackage("test", "2.3"));
                    repository.AddPackage(CreatePackage("test", "2.4.0-prerel"));
                    repository.AddPackage(CreatePackage("test", "3.2.0+taggedOnly"));
                });

                // Act
                var packages = serverRepository.GetPackages(ClientCompatibility.Default);

                // Assert
                Assert.Equal(1, packages.Count(p => p.SemVer1IsAbsoluteLatest));
                Assert.Equal("2.4.0-prerel", packages.First(p => p.SemVer1IsAbsoluteLatest).Version.ToString());
            }
        }

        [Fact]
        public void ServerPackageRepositorySemVer2IsAbsoluteLatest()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
                {
                    repository.AddPackage(CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.2-beta"));
                    repository.AddPackage(CreatePackage("test", "2.3"));
                    repository.AddPackage(CreatePackage("test", "2.4.0-prerel"));
                    repository.AddPackage(CreatePackage("test", "3.2.0+taggedOnly"));
                });

                // Act
                var packages = serverRepository.GetPackages(ClientCompatibility.Max);

                // Assert
                Assert.Equal(1, packages.Count(p => p.SemVer2IsAbsoluteLatest));
                Assert.Equal("3.2.0", packages.First(p => p.SemVer2IsAbsoluteLatest).Version.ToString());
            }
        }

        [Fact]
        public void ServerPackageRepositorySemVer2IsLatestOnlyPreRel()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
                {
                    repository.AddPackage(CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(CreatePackage("test", "2.2-beta+tagged"));
                });
                
                // Act
                var packages = serverRepository.GetPackages(ClientCompatibility.Max);

                // Assert
                Assert.Equal(0, packages.Count(p => p.SemVer2IsLatest));
            }
        }

        // TODO query for latest, do we really want caching?
        // [Fact]
        // public void ServerPackageRepositorySemVer1IsLatest()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test1", "1.0.0"));
        //             repository.AddPackage(CreatePackage("test1", "1.2.0+taggedOnly"));
        //             repository.AddPackage(CreatePackage("test1", "2.0.0-alpha"));
        //         });

        //         // Act
        //         var packages = serverRepository.GetPackages(ClientCompatibility.Default);

        //         // Assert
        //         Assert.Equal(1, packages.Count(p => p.SemVer1IsLatest));
        //         Assert.Equal("1.0.0", packages.First(p => p.SemVer1IsLatest).Version.ToString());
        //     }
        // }

        // TODO query for latest, do we really want caching?
        // [Fact]
        // public void ServerPackageRepositorySemVer2IsLatest()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
        //         {
        //             repository.AddPackage(CreatePackage("test", "1.11"));
        //             repository.AddPackage(CreatePackage("test", "1.9"));
        //             repository.AddPackage(CreatePackage("test", "2.0-alpha"));
        //             repository.AddPackage(CreatePackage("test1", "1.0.0"));
        //             repository.AddPackage(CreatePackage("test1", "1.2.0+taggedOnly"));
        //             repository.AddPackage(CreatePackage("test1", "2.0.0-alpha"));
        //         });

        //         // Act
        //         var packages = serverRepository.GetPackages(ClientCompatibility.Max);

        //         // Assert
        //         Assert.Equal(2, packages.Count(p => p.SemVer2IsLatest));
        //         Assert.Equal("1.11", packages
        //             .OrderBy(p => p.Id)
        //             .First(p => p.SemVer2IsLatest)
        //             .Version
        //             .ToString());
        //     }
        // }

        // [Fact]
        // public void ServerPackageRepositoryReadsDerivedData()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var package = CreatePackage("test", "1.0");
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config, repository =>
        //         {
        //             repository.AddPackage(package);
        //         });

        //         // Act
        //         var packages = serverRepository.GetPackages();
        //         var singlePackage = packages.Single() as ServerPackage;

        //         // Assert
        //         Assert.NotNull(singlePackage);
        //         Assert.Equal(package.GetStream().Length, singlePackage.PackageSize);
        //     }
        // }

        // [Fact]
        // public void ServerPackageRepositoryEmptyRepo()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         CreatePackage("test", "1.0");
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path,config);

        //         // Act
        //         var findPackage = serverRepository.FindPackage("test", new SemanticVersion("1.0"));
        //         var findPackagesById = serverRepository.FindPackagesById("test");
        //         var getPackages = serverRepository.GetPackages().ToList();
        //         var getPackagesWithDerivedData = serverRepository.GetPackages().ToList();
        //         var getUpdates = serverRepository.GetUpdates(Enumerable.Empty<IPackageName>(), true, true, Enumerable.Empty<FrameworkName>(), Enumerable.Empty<IVersionSpec>());
        //         var search = serverRepository.Search("test", true).ToList();
        //         var source = serverRepository.Source;

        //         // Assert
        //         Assert.Null(findPackage);
        //         Assert.Empty(findPackagesById);
        //         Assert.Empty(getPackages);
        //         Assert.Empty(getPackagesWithDerivedData);
        //         Assert.Empty(getUpdates);
        //         Assert.Empty(search);
        //         Assert.NotEmpty(source);
        //     }
        // }

        [Fact]
        public void ServerPackageRepositoryAddPackage()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config);

                // Act
                serverRepository.AddPackage(CreatePackage("Foo", "1.0.0"));

                // Assert
                Assert.True(serverRepository.Exists("Foo", NuGetVersion.Parse("1.0.0")));
            }
        }

        [Fact]
        public void ServerPackageRepositoryAddPackageSemVer2()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config);

                // Act
                serverRepository.AddPackage(CreatePackage("Foo", "1.0.0+foo"));

                // Assert
                Assert.True(serverRepository.Exists("Foo", NuGetVersion.Parse("1.0.0")));
            }
        }

        // [Fact]
        // public void ServerPackageRepositoryRemovePackageSemVer2()
        // {
        //     using (var temporaryDirectory = new TemporaryDirectory())
        //     {
        //         // Arrange
        //         var serverRepository = CreateServerPackageRepository(temporaryDirectory.Path, config);
        //         serverRepository.AddPackage(CreatePackage("Foo", "1.0.0+foo"));

        //         // Act
        //         serverRepository.RemovePackage("Foo", SemanticVersion.Parse("1.0.0+bar"));

        //         // Assert
        //         Assert.False(serverRepository.Exists("Foo", SemanticVersion.Parse("1.0.0")));
        //     }
        // }

        [Fact]
        public void ServerPackageRepositoryAddPackageRejectsDuplicatesWithSemVer2()
        {
            using (var temporaryDirectory = new TemporaryDirectory())
            {
                // Arrange
                config.AllowOverrideExistingPackageOnPush = false;
                var serverRepository = CreateServerPackageRepository(
                    temporaryDirectory.Path, config);
                var inFile = CreatePackage("Foo", "1.0.0-beta.1+foo");
                serverRepository.AddPackage(inFile);
                File.Delete(inFile.Path);

                // Act & Assert
                var actual = Assert.Throws<InvalidOperationException>(() =>
                    serverRepository.AddPackage(CreatePackage("Foo", "1.0.0-beta.1+bar")));
                Assert.Equal(
                    "Package Foo.1.0.0-beta.1 already exists. The server is configured to not allow overwriting packages that already exist.",
                    actual.Message);
            }
        }

        private static IPackage CreateMockPackage(string id, string version)
        {
            var package = new Mock<IPackage>();
            package.Setup(p => p.Id).Returns(id);
            package.Setup(p => p.Version).Returns(NuGetVersion.Parse(version));
            package.Setup(p => p.Listed).Returns(true);

            return package.Object;
        }

        private LocalPackageInfo CreatePackage(string id, string version, Action<PackageBuilder> builderSteps) {
            return LiGet.Tests.PackageHelper.CreatePackage(Path.Combine(tmpDir.Path,"test-input"), id, version, builderSteps);
        }

        private LocalPackageInfo CreatePackage(string id, string version)
        {
            return LiGet.Tests.PackageHelper.CreatePackage(Path.Combine(tmpDir.Path,"test-input"), id, version);
        }

        public static LocalPackageInfo GetPackageFromNupkgBytes(byte[] nupkgBytes)
        {
            using (var package = new PackageArchiveReader(new MemoryStream(nupkgBytes)))
            {
                var nuspec = package.NuspecReader;

                var packageHelper = new Func<PackageReaderBase>(() => new PackageArchiveReader(new MemoryStream(nupkgBytes)));
                var nuspecHelper = new Lazy<NuspecReader>(() => nuspec);

                return new LocalPackageInfo(
                    nuspec.GetIdentity(),
                    "in-memory",
                    DateTime.UtcNow,
                    nuspecHelper,
                    packageHelper
                );
            }
        }
    }
}
