using Ionic.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Runtime.Versioning;

namespace NuGet
{
    internal class ZipPackageFile : IPackageFile
    {
        private readonly Func<Stream> _streamFactory;
        private readonly FrameworkName _targetFramework;

        public ZipPackageFile(PackagePart part)
            : this(UriUtility.GetPath(part.Uri), part.GetStream().ToStreamFactory())
        {
        }

        public ZipPackageFile(IPackageFile file)
            : this(file.Path, file.GetStream().ToStreamFactory())
        {
        }

		public ZipPackageFile(ZipEntry part)
			: this(UriUtility.GetPath(new Uri(part.FileName, UriKind.Relative)), part)
		{			
		}

		protected ZipPackageFile(string path, ZipEntry entry)
		{
			using (MemoryStream ms = new MemoryStream())
			{
				entry.Extract(ms);
				_streamFactory = ms.ToStreamFactory();
			}

			Path = path;

			string effectivePath;
			_targetFramework = VersionUtility.ParseFrameworkNameFromFilePath(path, out effectivePath);
			EffectivePath = effectivePath;
		}

		protected ZipPackageFile(string path, Func<Stream> streamFactory)
        {
            Path = path;
            _streamFactory = streamFactory;

            string effectivePath;
            _targetFramework = VersionUtility.ParseFrameworkNameFromFilePath(path, out effectivePath);
            EffectivePath = effectivePath;
        }

        public string Path
        {
            get;
            private set;
        }

        public string EffectivePath
        {
            get;
            private set;
        }

        public FrameworkName TargetFramework
        {
            get
            {
                return _targetFramework;
            }
        }

        IEnumerable<FrameworkName> IFrameworkTargetable.SupportedFrameworks
        {
            get
            {
                if (TargetFramework != null)
                {
                    yield return TargetFramework;
                }
                yield break;
            }
        }

        public Stream GetStream()
        {
            return _streamFactory();
        }

        public override string ToString()
        {
            return Path;
        }
    }
}