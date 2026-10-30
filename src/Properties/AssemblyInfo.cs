using System;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using WinMemoryCleaner;

[assembly: AssemblyCompany(Constants.App.Author.Name)]
[assembly: AssemblyCopyright(Constants.App.License)]
[assembly: AssemblyDescription(Constants.App.Title)]
[assembly: AssemblyProduct(Constants.App.Name)]
[assembly: AssemblyTitle(Constants.App.Title)]
[assembly: AssemblyVersion("3.0.9.0")]
[assembly: ComVisible(false)]
[assembly: CLSCompliant(true)]
[assembly: Guid(Constants.App.Id)]
[assembly: NeutralResourcesLanguage(Constants.Windows.Locale.Name.English)]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.None)]

// The test project is signed with the same key, so it can access internal members.
[assembly: InternalsVisibleTo("WinMemoryCleaner.Tests, PublicKey=0024000004800000940000000602000000240000525341310004000001000100bd2d102191ea330b3f527d947e2d15f877d07bc2696f387c5e1862caa6f67d0cc65bade0279678a91df880a3ad616c437f9208d450ca4c108d722cc9986beb9cd8d46bad2e1902aaf5a45beaafaeedaae129e86453549bf7e6a1145538fa7c38eadd19acf75871d77725c2cb0e8845520ffdb8fc0d8f81f6f61ab25ac29578db")]

