# Bundled data attribution

## ar-zipcodes.json — Arkansas ZIP → city / county / coordinates

Source: **GeoNames** postal-code dataset (`export/zip/US.zip`), filtered to Arkansas
(`admin1 == "AR"`). 706 ZIPs, all 75 Arkansas counties.

Each entry is keyed by ZIP:

```json
"71601": { "city": "Pine Bluff", "county": "Jefferson", "lat": 34.209, "lng": -91.9859 }
```

Licensed under **Creative Commons Attribution 4.0** (CC BY 4.0):
https://creativecommons.org/licenses/by/4.0/ · https://www.geonames.org/

To regenerate, download the current GeoNames `US.zip`, extract `US.txt` (tab-separated),
and keep rows where field 5 == `AR`, emitting `{zip: {city, county, lat, lng}}` from
fields 2 (zip), 3 (city), 6 (county), 10 (lat), 11 (lng).
