import durandalRouter = require("plugins/router");
import database = require("models/database");
import appUrl = require("common/appUrl");
import viewModelBase = require("viewmodels/viewModelBase");

class settings extends viewModelBase {

    router: DurandalRootRouter = null;
    isOnSystemDatabase: KnockoutComputed<boolean>;
    isOnUserDatabase: KnockoutComputed<boolean>;

    constructor() {
        super();

        this.isOnSystemDatabase = ko.computed(() => this.activeDatabase() && this.activeDatabase().isSystem);
        this.isOnUserDatabase = ko.computed(() => this.activeDatabase() && !this.isOnSystemDatabase());

        var apiKeyRoute = { route: 'settings/apiKeys', moduleId: 'viewmodels/apiKeys', title: 'API Keys', nav: true, hash: appUrl.forApiKeys() };
        var windowsAuthRoute = { route: 'settings/windowsAuth', moduleId: 'viewmodels/windowsAuth', title: 'Windows Authentication', nav: true, hash: appUrl.forWindowsAuth() };
        var databaseSettingsRoute = { route: 'settings/databaseSettings', moduleId: 'viewmodels/databaseSettings', title: 'Database Settings', nav: true, hash: appUrl.forCurrentDatabase().databaseSettings };
        var periodicBackupRoute = { route: 'settings/periodicBackup', moduleId: 'viewmodels/periodicBackup', title: 'Periodic Backup', nav: true, hash: appUrl.forCurrentDatabase().periodicBackup };
        this.router = durandalRouter.createChildRouter()
            .map([
                apiKeyRoute,
                windowsAuthRoute,
                databaseSettingsRoute,
                periodicBackupRoute
            ])
            .buildNavigationModel();

        
        this.router.guardRoute = (instance: Object, instruction: DurandalRouteInstruction) => this.getValidRoute(instance, instruction);
    }

    /**
    * Checks whether the route can be navigated to. Returns true if it can be navigated to, or an redirect URI if it can't be navigated to.
    * This is used for preventing a navigating to system-only pages when the current databagse is non-system, and vice-versa.
    */
    getValidRoute(instance: Object, instruction: DurandalRouteInstruction): any {

        var isSystemDbOnlyPath = instruction.fragment.indexOf("windowsAuth") >= 0 || instruction.fragment.indexOf("apiKeys") >= 0;
        var isUserDbOnlyPath = !isSystemDbOnlyPath;
        if (isSystemDbOnlyPath && !this.activeDatabase().isSystem) {
            return appUrl.forCurrentDatabase().databaseSettings();
        } else if (isUserDbOnlyPath && this.activeDatabase().isSystem) {
            return appUrl.forApiKeys();
        }

        return true;
    }

    activate(args) {
        super.activate(args);
    }

    routeIsVisible(route: DurandalRouteConfiguration) {
        if (route.title === "Periodic Backup" || route.title === "Database Settings") {
            // Periodic backup and database settings are visible only when we're on a user database.
            return this.isOnUserDatabase();
        } else {
            // API keys and Windows Auth are visible only when we're on the system database.
            return this.isOnSystemDatabase();
        }
    }
}

export = settings;