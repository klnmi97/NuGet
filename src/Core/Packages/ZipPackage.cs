using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Runtime.Versioning;
using NuGet.Resources;
using Ionic.Zip;
using System.Text.RegularExpressions;

namespace NuGet
{
    public class ZipPackage : LocalPackage
    {
        private const string CacheKeyFormat = "NUGET_ZIP_PACKAGE_{0}_{1}{2}";
        private const string AssembliesCacheKey = "ASSEMBLIES";
        private const string FilesCacheKey = "FILES";

        private readonly bool _enableCaching;

        private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(15);

        // paths to exclude
        private static readonly string[] ExcludePaths = new[] { "_rels", "package" };

        // We don't store the stream itself, just a way to open the stream on demand
        // so we don't have to hold on to that resource
        private readonly Func<Stream> _streamFactory;
		private String filePath;

        public ZipPackage(string filePath)
            : this(filePath, enableCaching: false)
        {
        }

        public ZipPackage(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            _enableCaching = false;
            _streamFactory = stream.ToStreamFactory();
			EnsureManifest();
        }

        private ZipPackage(string filePath, bool enableCaching)
        {
            if (String.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException(CommonResources.Argument_Cannot_Be_Null_Or_Empty, "filePath");
            }
            _enableCaching = enableCaching;
            _streamFactory = () => File.OpenRead(filePath);
			this.filePath = filePath;
            EnsureManifest();
        }

        internal ZipPackage(Func<Stream> streamFactory, bool enableCaching)
        {
            if (streamFactory == null)
            {
                throw new ArgumentNullException("streamFactory");
            }
            _enableCaching = enableCaching;
            _streamFactory = streamFactory;
            EnsureManifest();
        }

        public override Stream GetStream()
        {
            return _streamFactory();
        }

        public override IEnumerable<FrameworkName> GetSupportedFrameworks()
        {
            IEnumerable<FrameworkName> fileFrameworks;
            IEnumerable<IPackageFile> cachedFiles;
            if (_enableCaching && MemoryCache.Instance.TryGetValue(GetFilesCacheKey(), out cachedFiles))
            {
                fileFrameworks = cachedFiles.Select(c => c.TargetFramework);
            }
            else
            {
                using (Stream stream = _streamFactory())
                {
					using (ZipFile zip = ZipFile.Read(stream))
					{
						string effectivePath;
						fileFrameworks = from part in zip.Entries
										 where IsPackageFile(new Uri(part.FileName, UriKind.Relative))
										 select VersionUtility.ParseFrameworkNameFromFilePath(UriUtility.GetPath(new Uri(part.FileName, UriKind.Relative)), out effectivePath);
					}
				}
            }

            return base.GetSupportedFrameworks()
                       .Concat(fileFrameworks)
                       .Where(f => f != null)
                       .Distinct();
        }

        protected override IEnumerable<IPackageAssemblyReference> GetAssemblyReferencesCore()
        {
            if (_enableCaching)
            {
                return MemoryCache.Instance.GetOrAdd(GetAssembliesCacheKey(), GetAssembliesNoCache, CacheTimeout);
            }

            return GetAssembliesNoCache();
        }

        protected override IEnumerable<IPackageFile> GetFilesBase()
        {
            if (_enableCaching)
            {
                return MemoryCache.Instance.GetOrAdd(GetFilesCacheKey(), GetFilesNoCache, CacheTimeout);
            }
            return GetFilesNoCache();
        }

        private List<IPackageAssemblyReference> GetAssembliesNoCache()
        {
            return (from file in GetFiles()
                    where IsAssemblyReference(file.Path)
                    select (IPackageAssemblyReference)new ZipPackageAssemblyReference(file)).ToList();
        }

        private List<IPackageFile> GetFilesNoCache()
        {
            using (Stream stream = _streamFactory())
            {
				using (ZipFile zip = ZipFile.Read(stream))
				{
					return (from part in zip.Entries
							where IsPackageFile(new Uri(part.FileName, UriKind.Relative))
							select (IPackageFile)new ZipPackageFile(part)).ToList();
				}
			}
        }

        private void EnsureManifest()
        {
			using (Stream stream = _streamFactory())
			{
				using (MemoryStream manifestStream = new MemoryStream())
				{
					using (ZipFile zip = ZipFile.Read(stream))
					{
						foreach (ZipEntry e in zip)
						{
							// Manifest is in the .nuspec file.
							if (e.FileName.EndsWith(".nuspec"))
							{
								e.Extract(manifestStream);
							}
						}
					}

					// Let's remove the XML declaration for the XML parser.
					string manifest = System.Text.Encoding.UTF8.GetString(manifestStream.ToArray());
					string manafiestWithoutDeclaration = RemoveFirstLines(manifest, 1);

					byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(manafiestWithoutDeclaration);
					using (MemoryStream fixedStream = new MemoryStream(byteArray))
					{
						ReadManifest(fixedStream);
					}
				}
			}
		}

		private string RemoveFirstLines(string text, int linesCount)
		{
			var lines = Regex.Split(text, "\r\n|\r|\n").Skip(linesCount);
			return string.Join(Environment.NewLine, lines.ToArray());
		}

		private string GetFilesCacheKey()
        {
            return String.Format(CultureInfo.InvariantCulture, CacheKeyFormat, FilesCacheKey, Id, Version);
        }

        private string GetAssembliesCacheKey()
        {
            return String.Format(CultureInfo.InvariantCulture, CacheKeyFormat, AssembliesCacheKey, Id, Version);
        }

        internal static bool IsPackageFile(PackagePart part)
        {
            string path = UriUtility.GetPath(part.Uri);
            string directory = Path.GetDirectoryName(path);

            // We exclude any opc files and the manifest file (.nuspec)
            return !ExcludePaths.Any(p => directory.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                   !PackageHelper.IsManifest(path);
        }

		internal static bool IsPackageFile(Uri uri)
		{
			string path = UriUtility.GetPath(uri);
			string directory = Path.GetDirectoryName(path);

			// We exclude any opc files and the manifest file (.nuspec)
			return !ExcludePaths.Any(p => directory.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
				   !PackageHelper.IsManifest(path);
		}

		internal static void ClearCache(IPackage package)
        {
            var zipPackage = package as ZipPackage;

            // Remove the cache entries for files and assemblies
            if (zipPackage != null)
            {
                MemoryCache.Instance.Remove(zipPackage.GetAssembliesCacheKey());
                MemoryCache.Instance.Remove(zipPackage.GetFilesCacheKey());
            }
        }
    }
}