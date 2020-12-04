using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Newtonsoft.Json;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Configuration;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Services.Common;
using Nop.Services.Installation;
using Nop.Services.Plugins;
using Nop.Services.Security;
using Nop.Web.Framework.Security;
using Nop.Web.Infrastructure.Installation;
using Nop.Web.Models.Install;

namespace Nop.Web.Controllers
{
    public partial class InstallController : Controller
    {
        #region Fields

        private readonly AppSettings _appSettings;
        private readonly IInstallationLocalizationService _locService;
        private readonly INopFileProvider _fileProvider;

        #endregion

        #region Ctor

        public InstallController(AppSettings appSettings,
            IInstallationLocalizationService locService,
            INopFileProvider fileProvider)
        {
            _appSettings = appSettings;
            _locService = locService;
            _fileProvider = fileProvider;
        }

        #endregion

        #region Utilites

        private InstallModel PrepareCulturesList (InstallModel model)
        {
            if (model.InstallRegionalResources)
            {
                model.AvailableCountries.AddRange(
                CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                    .OrderBy(cultureInfo => new RegionInfo(cultureInfo.Name).DisplayName)
                    .Where(cultureInfo => cultureInfo.TwoLetterISOLanguageName.Count() == 2)
                    .Select(cultureInfo => new SelectListItem
                    {
                        Value = cultureInfo.Name,
                        Text = $"{new RegionInfo(cultureInfo.Name).DisplayName} ({cultureInfo.IetfLanguageTag})",
                        Selected = cultureInfo.Name == _locService.GetBrowserCulture()
                    })
                );
            }

            return model;
        }

        private InstallModel PrepareLanguagesList (InstallModel model)
        {
            foreach (var lang in _locService.GetAvailableLanguages())
            {
                model.AvailableLanguages.Add(new SelectListItem
                {
                    Value = Url.Action("ChangeLanguage", "Install", new { language = lang.Code }),
                    Text = lang.Name,
                    Selected = _locService.GetCurrentLanguage().Code == lang.Code
                });
            }

            return model;
        }

        private InstallModel PrepareAvailableDataProviders (InstallModel model)
        {
            model.AvailableDataProviders.AddRange(
                _locService.GetAvailableProviderTypes()
                .OrderBy(v => v.Value)
                .Select(pt => new SelectListItem
                {
                    Value = pt.Key.ToString(),
                    Text = pt.Value
                }));

            return model;
        }

        #endregion

        #region Methods

        public virtual IActionResult Index()
        {
            if (DataSettingsManager.DatabaseIsInstalled)
                return RedirectToRoute("Homepage");

            var model = new InstallModel
            {
                AdminEmail = "admin@yourStore.com",
                InstallSampleData = false,
                InstallRegionalResources = _appSettings.InstallationConfig.InstallRegionalResources,

                //fast installation service does not support SQL compact
                DisableSampleDataOption = _appSettings.InstallationConfig.DisableSampleData,
                CreateDatabaseIfNotExists = false,
                ConnectionStringRaw = false,
                DataProvider = DataProviderType.SqlServer
            };

            PrepareAvailableDataProviders(model);
            PrepareLanguagesList(model);
            PrepareCulturesList(model);

            return View(model);
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public virtual async Task<IActionResult> Index(InstallModel model)
        {
            if (DataSettingsManager.DatabaseIsInstalled)
                return RedirectToRoute("Homepage");

            model.DisableSampleDataOption = _appSettings.InstallationConfig.DisableSampleData;
            model.InstallRegionalResources = _appSettings.InstallationConfig.InstallRegionalResources;

            PrepareAvailableDataProviders(model);
            PrepareLanguagesList(model);            
            PrepareCulturesList(model);

            //Consider granting access rights to the resource to the ASP.NET request identity. 
            //ASP.NET has a base process identity 
            //(typically {MACHINE}\ASPNET on IIS 5 or Network Service on IIS 6 and IIS 7, 
            //and the configured application pool identity on IIS 7.5) that is used if the application is not impersonating.
            //If the application is impersonating via <identity impersonate="true"/>, 
            //the identity will be the anonymous user (typically IUSR_MACHINENAME) or the authenticated request user.
            var webHelper = EngineContext.Current.Resolve<IWebHelper>();
            //validate permissions
            var dirsToCheck = FilePermissionHelper.GetDirectoriesWrite();
            foreach (var dir in dirsToCheck)
                if (!FilePermissionHelper.CheckPermissions(dir, false, true, true, false))
                    ModelState.AddModelError(string.Empty, string.Format(_locService.GetResource("ConfigureDirectoryPermissions"), CurrentOSUser.FullName, dir));

            var filesToCheck = FilePermissionHelper.GetFilesWrite();
            foreach (var file in filesToCheck)
            {
                if (!_fileProvider.FileExists(file))
                    continue;

                if (!FilePermissionHelper.CheckPermissions(file, false, true, true, true))
                    ModelState.AddModelError(string.Empty, string.Format(_locService.GetResource("ConfigureFilePermissions"), CurrentOSUser.FullName, file));
            }

            if (!ModelState.IsValid)
                return View(model);

            try
            {
                var dataProvider = DataProviderManager.GetDataProvider(model.DataProvider);

                var connectionString = model.ConnectionStringRaw ? model.ConnectionString : dataProvider.BuildConnectionString(model);

                if (string.IsNullOrEmpty(connectionString))
                    throw new Exception(_locService.GetResource("ConnectionStringWrongFormat"));

                DataSettingsManager.SaveSettings(new DataSettings
                {
                    DataProvider = model.DataProvider,
                    ConnectionString = connectionString
                }, _fileProvider);

                DataSettingsManager.LoadSettings(reloadSettings: true);

                if (model.CreateDatabaseIfNotExists)
                {
                    try
                    {
                        dataProvider.CreateDatabase(model.Collation);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(string.Format(_locService.GetResource("DatabaseCreationError"), ex.Message));
                    }
                }
                else
                {
                    //check whether database exists
                    if (!dataProvider.DatabaseExists())
                        throw new Exception(_locService.GetResource("DatabaseNotExists"));
                }

                dataProvider.InitializeDatabase();

                //try to get CultureInfo
                var selectedCountryCulture = NopCommonDefaults.DefaultLanguageCulture;
                try
                {
                    selectedCountryCulture = new CultureInfo(model.Country);
                }
                catch { }

                var installRegionalResources = _appSettings.InstallationConfig.InstallRegionalResources;
                var cultureInfo = installRegionalResources ? selectedCountryCulture : null;

                var downloadUrl = string.Empty;
                if (installRegionalResources)
                {
                    //get language pack
                    if (cultureInfo != null && cultureInfo != NopCommonDefaults.DefaultLanguageCulture)
                    {
                        try
                        {
                            var languageCode = _locService.GetCurrentLanguage().Code[0..2];
                            var client = EngineContext.Current.Resolve<NopHttpClient>();
                            var resultString = await client.InstallationCompletedAsync(model.AdminEmail, languageCode, cultureInfo.Name);
                            var result = JsonConvert.DeserializeAnonymousType(resultString,
                                new { Message = string.Empty, LanguagePack = new { Culture = string.Empty, Progress = 0, DownloadLink = string.Empty } });
                            if (result.LanguagePack.Progress > NopCommonDefaults.LanguagePackMinTranslationProgressToInstall)
                            {
                                downloadUrl = result.LanguagePack.DownloadLink;
                            }
                        }
                        catch { }
                    }

                    //upload CLDR
                    var uploadService = EngineContext.Current.Resolve<IUploadService>();
                    uploadService.UploadLocalePattern(cultureInfo);
                }

                //now resolve installation service
                var installationService = EngineContext.Current.Resolve<IInstallationService>();

                installationService.InstallRequiredData(model.AdminEmail, model.AdminPassword, downloadUrl,
                    installRegionalResources ? new RegionInfo(model.Country) : null,
                    installRegionalResources ? cultureInfo : null);

                if (model.InstallSampleData)
                    installationService.InstallSampleData(model.AdminEmail);

                //prepare plugins to install
                var pluginService = EngineContext.Current.Resolve<IPluginService>();
                pluginService.ClearInstalledPluginsList();

                var pluginsIgnoredDuringInstallation = new List<string>();
                if (!string.IsNullOrEmpty(_appSettings.InstallationConfig.DisabledPlugins))
                {
                    pluginsIgnoredDuringInstallation = _appSettings.InstallationConfig.DisabledPlugins
                        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(pluginName => pluginName.Trim()).ToList();
                }

                var plugins = pluginService.GetPluginDescriptors<IPlugin>(LoadPluginsMode.All)
                    .Where(pluginDescriptor => !pluginsIgnoredDuringInstallation.Contains(pluginDescriptor.SystemName))
                    .OrderBy(pluginDescriptor => pluginDescriptor.Group).ThenBy(pluginDescriptor => pluginDescriptor.DisplayOrder)
                    .ToList();

                foreach (var plugin in plugins)
                {
                    pluginService.PreparePluginToInstall(plugin.SystemName, checkDependencies: false);
                }

                //register default permissions
                //var permissionProviders = EngineContext.Current.Resolve<ITypeFinder>().FindClassesOfType<IPermissionProvider>();
                var permissionProviders = new List<Type> { typeof(StandardPermissionProvider) };
                foreach (var providerType in permissionProviders)
                {
                    var provider = (IPermissionProvider)Activator.CreateInstance(providerType);
                    EngineContext.Current.Resolve<IPermissionService>().InstallPermissions(provider);
                }

                return View(new InstallModel { RestartUrl = Url.RouteUrl("Homepage") });

            }
            catch (Exception exception)
            {
                //reset cache
                DataSettingsManager.ResetCache();

                var staticCacheManager = EngineContext.Current.Resolve<IStaticCacheManager>();
                staticCacheManager.Clear();

                //clear provider settings if something got wrong
                DataSettingsManager.SaveSettings(new DataSettings(), _fileProvider);

                ModelState.AddModelError(string.Empty, string.Format(_locService.GetResource("SetupFailed"), exception.Message));
            }

            return View(model);
        }

        public virtual IActionResult ChangeLanguage(string language)
        {
            if (DataSettingsManager.DatabaseIsInstalled)
                return RedirectToRoute("Homepage");

            _locService.SaveCurrentLanguage(language);

            //Reload the page
            return RedirectToAction("Index", "Install");
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public virtual IActionResult RestartInstall()
        {
            if (DataSettingsManager.DatabaseIsInstalled)
                return RedirectToRoute("Homepage");

            return View("Index", new InstallModel { RestartUrl = Url.Action("Index", "Install") });
        }

        public virtual IActionResult RestartApplication()
        {
            if (DataSettingsManager.DatabaseIsInstalled)
                return RedirectToRoute("Homepage");

            //restart application
            EngineContext.Current.Resolve<IWebHelper>().RestartAppDomain();

            return new EmptyResult();
        }

        #endregion
    }
}