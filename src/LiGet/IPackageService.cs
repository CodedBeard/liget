using System.Collections.Generic;
using LiGet.Models;
using LiGet.NuGet.Server.Infrastructure;
using NuGet.Protocol;

namespace LiGet
{
    public class HostedPackage {
        private ODataPackage _packageInfo;
        // TODO accessor to nupkg, and anything else that web service may need
        public HostedPackage(ODataPackage packageInfo) {
            _packageInfo = packageInfo;
        }
    }

    /// <summary>
    /// Provides package queries and operations for the web API.
    /// </summary>
    public interface IPackageService
    {
        IEnumerable<HostedPackage> FindPackagesById(string id, ClientCompatibility compatibility);
    }
}