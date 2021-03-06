﻿// The header functionality on all pages (requires bootstrap for the logged in user dropdown which may be present)
define(["knockout", "jquery", "lib/knockout-extenders", "bootstrap", "app/common"], function (ko, $) {
    // A view model for the header/navbar
    function headerViewModel() {
        var self = this;

        // The value in the search box, throttled so that we don't constantly update as they are typing
        self.searchValue = ko.observable("").extend({ rateLimit: { method: "notifyWhenChangesStop", timeout: 400 } });

        // A list of query suggestions, using the async extender to provide a value when a query is in-flight
        self.querySuggestions = ko.computed(function() {
            var search = self.searchValue();
            if (search.length === 0)
                return [];

            return $.getJSON("/search/suggestqueries", { query: search, pageSize: 10 }).then(function (response) {
                // If we failed for some reason, just return an empty array
                if (!response.success)
                    return [];

                return response.data.suggestions;
            });
        }).extend({ async: [] });

        // Whether or not to show the "What is this?" section
        self.showWhatIsThis = ko.observable(false);

        // Toggle the what is this section
        self.toggleWhatIsThis = function() {
            self.showWhatIsThis(!self.showWhatIsThis());
        };
    }


    // Bind the search box when dom is ready
    $(function () {
        ko.applyBindings(new headerViewModel(), $("#header").get(0));
    });
});