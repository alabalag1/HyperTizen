// Runs as TizenBrew's service file, or as a page script in the standalone app; skip where there's no Tizen API.
const interval = typeof tizen === 'undefined' ? null : setInterval(() => {
    try {
        tizen.application.getAppInfo('io.gh.reisxd.HyperTizen');
        tizen.application.launch(
            'io.gh.reisxd.HyperTizen',
            function () {
                console.log('Launch Service succeeded');
                clearInterval(interval);
            },
            function (e) {
                console.log("Launch Service failed: " + e.message);
            }
        );
    } catch (e) {
        console.log('App not found');
    }    
}, 1000);