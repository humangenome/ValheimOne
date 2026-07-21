(function () {
    "use strict";

    function svg(content) {
        return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" ' +
            'stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" ' +
            'aria-hidden="true" focusable="false">' + content + "</svg>";
    }

    window.VO_ICONS = {
        spawn: svg(
            '<path d="M3.5 20.5h17"/>' +
            '<path d="M5 20.5l.7-10 2.3-2 2.2 2-.7 10"/>' +
            '<path d="M9.5 20.5l1-14.2L12 4l1.5 2.3 1 14.2"/>' +
            '<path d="M14.5 20.5l-.7-10 2.2-2 2.3 2 .7 10"/>'
        ),
        trader: svg(
            '<path d="M12 3v14M7 6h10M8.5 6 5 12m10.5-6L19 12"/>' +
            '<path d="M3 12h4c0 2-1 3-2 3s-2-1-2-3Zm14 0h4c0 2-1 3-2 3s-2-1-2-3Z"/>' +
            '<path d="M9 18c0-2 1.3-3 3-3s3 1 3 3c0 1.7-1.2 3-3 3s-3-1.3-3-3Z"/>'
        ),
        misc: svg(
            '<path d="m12 2.8 8.2 9.2-8.2 9.2L3.8 12 12 2.8Z"/>' +
            '<path d="m12 7 4.3 5-4.3 5-4.3-5L12 7Z"/>' +
            '<circle cx="12" cy="12" r="1.2"/>'
        ),

        boss_eikthyr: svg(
            '<path d="M9.2 8.5 6.5 6 5 2.8M7.3 7 4 6.3 2.8 4M14.8 8.5 17.5 6 19 2.8M16.7 7l3.3-.7L21.2 4"/>' +
            '<path d="M7.8 8.2c1.3-1 2.7-1.4 4.2-1.4s2.9.4 4.2 1.4l-1 7.2-3.2 4-3.2-4-1-7.2Z"/>' +
            '<path d="m9.5 11 1 .5m4-0.5-1 .5M10.5 16h3L12 18l-1.5-2Z"/>'
        ),
        boss_elder: svg(
            '<path d="M12 3v18M12 7 7 4M12 9l5-4M8 8 4 7m12 1 4-2M8.5 10.5 6 15m9.5-4.5L18 15"/>' +
            '<path d="M9 9c-.6 4-.4 8-2.2 12M15 9c.6 4 .4 8 2.2 12M6.8 21 12 18l5.2 3"/>' +
            '<path d="M9.7 12.5h4.6M10.5 15.2h3"/>'
        ),
        boss_bonemass: svg(
            '<path d="M5.5 18.5c-2-1.2-2-4 .1-5.1-.8-2.4.9-4.8 3.3-4.5.8-2.7 5.1-2.7 6.2 0 2.6-.3 4 2.6 3.1 4.7 2 1.5 1.2 4.3-.8 5.2"/>' +
            '<path d="M5.5 18.5c1.5 1.5 2.7-.1 4 .7 1.8 1.1 3.2-1 4.8-.1 1.2.7 2.1.4 3.1-.3M8 13.2l1.4-.5m6.6.5-1.4-.5M10 16h4"/>' +
            '<path d="M7 19.2V22m5-2.3V22m4-3v2"/>'
        ),
        boss_moder: svg(
            '<path d="M4 18c2.4-2.5 3.1-6.5 2.2-10.4L11 10l3.2-6.5 1.2 7 4.6 2.8-4.5 1.2-2 5-3.2-3L4 18Z"/>' +
            '<path d="M6.2 7.6 3 5l.8 7M11 10l2.5 2m2-1.3 1 .4M8 17l-2.5 3.5"/>' +
            '<path d="M10.4 13.2h.1"/>'
        ),
        boss_yagluth: svg(
            '<path d="m7.5 7-1-4 3 2 2.5-3 2.5 3 3-2-1 4"/>' +
            '<path d="M6.5 7h11v6.5c0 2.1-1.6 3.7-3.5 4.5v3h-4v-3c-1.9-.8-3.5-2.4-3.5-4.5V7Z"/>' +
            '<path d="m9 11 1.5 1M15 11l-1.5 1M10 15h4m-2 3v3"/>'
        ),
        boss_queen: svg(
            '<path d="M9 5 6 2m9 3 3-3M8 6l4-2 4 2 2 6-2 5-4 3-4-3-2-5 2-6Z"/>' +
            '<path d="M8 7 4 9m12-2 4 2M7 12l-4 3m14-3 4 3M9 17l-3 4m9-4 3 4"/>' +
            '<path d="M9.5 9.5h.1m4.8 0h.1M9.5 13h5M12 4v16"/>'
        ),
        boss_fader: svg(
            '<path d="M7 8C4 6 6 3 8 4c0-2 3-2.5 4-1 1-1.5 4-1 4 1 2-1 4 2 1 4"/>' +
            '<path d="M6.5 8.5c0-1.7 2.5-2.5 3.2-1 .2-2.2 4.4-2.2 4.6 0 .7-1.5 3.2-.7 3.2 1v5c0 2-1.4 3.6-3.2 4.4V21H9.7v-3.1c-1.8-.8-3.2-2.4-3.2-4.4v-5Z"/>' +
            '<path d="m9 12 1.4 1m4.6-1-1.4 1M10 16h4m-2 2v3"/>'
        ),
        boss: svg(
            '<path d="M5 7c0-2 1-3.5 3-4-.6 2.4.2 4 2.2 5M19 7c0-2-1-3.5-3-4 .6 2.4-.2 4-2.2 5"/>' +
            '<path d="M8 9h8l1 6H7l1-6ZM5 15h14v3H5v-3Zm-2 3h18v3H3v-3Z"/>' +
            '<path d="m12 8-1.5 3 1.5 2 1.5-2L12 8Z"/>'
        ),

        dungeon_crypt: svg(
            '<path d="M4 21V11a8 8 0 0 1 16 0v10H4Z"/>' +
            '<path d="M8 21V11a4 4 0 0 1 8 0v10M4 15h4m8 0h4M6 8h2m8 0h2"/>' +
            '<path d="M11 14h2"/>'
        ),
        dungeon_sunkencrypt: svg(
            '<path d="M5 15v-4a7 7 0 0 1 14 0v4M8.5 15v-4a3.5 3.5 0 0 1 7 0v4"/>' +
            '<path d="M2.5 16.5c1.4-1 2.8-1 4.2 0s2.8 1 4.2 0 2.8-1 4.2 0 2.8 1 4.2 0M2.5 20c1.4-1 2.8-1 4.2 0s2.8 1 4.2 0 2.8-1 4.2 0 2.8 1 4.2 0"/>'
        ),
        dungeon_trollcave: svg(
            '<path d="m3 20 2-9 3-1 1-5 3 2 3-3 1 5 3 2 2 9H3Z"/>' +
            '<path d="M7.5 20c.2-5.5 2-8 4.5-8s4.3 2.5 4.5 8M9 14l1 2m5-2-1 2"/>' +
            '<path d="m10 12 2-2 2 2"/>'
        ),
        dungeon_frostcave: svg(
            '<path d="m3 20 2-10 4-5 3 2 3-4 4 7 2 10H3Z"/>' +
            '<path d="M7 20c.2-5.3 2-8 5-8s4.8 2.7 5 8M8 9l1.4 4L12 9l2.2 4L16 8"/>' +
            '<path d="M10 20v-3m4 3v-5"/>'
        ),
        dungeon_mine: svg(
            '<path d="M3 21h18M5 21V6h14v15M5 9h14M8 9v12m8-12v12"/>' +
            '<path d="m5 6 7-3 7 3M9 21l3-6 3 6M10 18h4"/>'
        ),
        dungeon_ashlands: svg(
            '<path d="M3 21V8h4V5h4v3h4V5h4v8M3 13h16v8H3Z"/>' +
            '<path d="M8 21v-4a4 4 0 0 1 8 0v4M19 21l2-5-2-2"/>' +
            '<path d="M20 13c-2-2 .5-3.5 0-6 2 1.5 3 4 0 6Z"/>'
        ),

        spawner_greydwarf: svg(
            '<path d="M4 20c2-3 3-6 3-10m13 10c-2-3-3-6-3-10M7 13l-4-2m4-2L5 5m12 8 4-2m-4-2 2-4"/>' +
            '<path d="M5 20c2-4 12-4 14 0M7 17c2-3 8-3 10 0M9 14c1.5-2 4.5-2 6 0"/>' +
            '<path d="m10 9 2-3 2 3-2 3-2-3Z"/>'
        ),
        spawner_bonepile: svg(
            '<path d="m5 8 2-2m-2 0 2 2m10 0 2-2m-2 0 2 2M9 6h6M7 11h10M5 16h14M8 21h8"/>' +
            '<path d="M9 4c0-1 1-2 3-2s3 1 3 2-1 2-3 2-3-1-3-2ZM7 9c-1 0-2 1-2 2s1 2 2 2m10-4c1 0 2 1 2 2s-1 2-2 2M5 14c-1 0-2 1-2 2s1 2 2 2m14-4c1 0 2 1 2 2s-1 2-2 2"/>' +
            '<path d="M8 18c-1 0-2 .7-2 1.5S7 21 8 21m8-3c1 0 2 .7 2 1.5S17 21 16 21"/>'
        ),
        spawner_draugrpile: svg(
            '<path d="M3 21c1.5-4 4-5 6-4M21 21c-1.5-4-4-5-6-4M6 21c1-2 3-3 6-3s5 1 6 3"/>' +
            '<path d="M10 18V9.5c0-1.2-1.6-1.2-1.6 0V13M10 11V6.8c0-1.3 1.8-1.3 1.8 0V11M11.8 10V6c0-1.3 1.8-1.3 1.8 0v5M13.6 10V7c0-1.2 1.7-1.2 1.7 0v5.5c0 3-1 4.6-3.3 5.5"/>' +
            '<path d="M10 13 8 12c-1.6-.8-2.4 1.2-1 2.3L10 17"/>'
        ),
        spawner_firehole: svg(
            '<path d="M4 20c2-2 4-2 6 0s4 2 6 0 3-2 4-1M7 16c2-1 3-1 5 0s3 1 5 0"/>' +
            '<path d="M12 14c-3-2.2-2.5-4.6-.5-6.3-.2 1.5.9 2.3 1.5 1.2 1.3-2.2-.8-3.7.7-6 3.8 3.2 4.5 8.3-1.7 11.1Z"/>'
        ),
        spawner_charred: svg(
            '<path d="M4 20h16M6 20l2-7 4-3 4 3 2 7M8 15h8M10 12l2 3 2-3"/>' +
            '<path d="M12 10c-2.4-1.8-2-3.8-.4-5.2-.1 1.2.8 1.8 1.3.9 1-1.8-.7-3 .5-4.7 3 2.5 3.6 6.6-1.4 9Z"/>'
        ),
        spawner_other: svg(
            '<circle cx="12" cy="12" r="8.5"/>' +
            '<path d="m12 5 2 4 4 .5-3 3 1 4.5-4-2-4 2 1-4.5-3-3L10 9l2-4Z"/>' +
            '<circle cx="12" cy="12" r="1.2"/>'
        ),

        ore_copper: svg(
            '<path d="M3 19 5.5 12l5-2 3 3 4-1 3.5 7H3Z"/>' +
            '<path d="m5.5 12 4 3 4-2m0 0 1.5 6m2.5-7-2.5 3M7 18l2.5-3 2.5 4"/>' +
            '<path d="M6.5 9 9 6l4 .5 1.5 3"/>'
        ),
        ore_tin: svg(
            '<path d="M7 17c0-2 2-3.5 5-3.5s5 1.5 5 3.5-2 3-5 3-5-1-5-3Z"/>' +
            '<path d="M9 12c0-1.6 1.2-2.7 3-2.7s3 1.1 3 2.7-1.2 2.2-3 2.2S9 13.6 9 12Zm1.5-4.5c0-1 1-1.8 2.3-1.8s2.2.8 2.2 1.8-1 1.8-2.3 1.8-2.2-.8-2.2-1.8Z"/>' +
            '<path d="M3 21c2-1 4-1 6 0s4 1 6 0 4-1 6 0"/>'
        ),
        ore_iron: svg(
            '<path d="M3 20c1-4 4-6 8-5 3-2 8 0 10 5H3Z"/>' +
            '<path d="m8 15-2-5m-2-1 6 2M5 8l-2-1m6 4 2-1"/>' +
            '<path d="M7 18h.1m4.9 0h.1m4.9-.5h.1"/>'
        ),
        ore_silver: svg(
            '<path d="m4 19 2-10 6-5 6 4 2 11H4Z"/>' +
            '<path d="m12 4-1 6 3 2-3 3 1 4M6 9l5 1m3 2 4-4m-7 7-4 4"/>'
        ),
        ore_obsidian: svg(
            '<path d="m5 20 4-14 3 5 3-8 4 17H5Z"/>' +
            '<path d="M9 6v14m6-17-3 17m0-9-3 4m6-6 3 6"/>'
        ),
        ore_meteorite: svg(
            '<path d="m15 3 1.1 3.1L19 7.5l-2.9 1.4L15 12l-1.1-3.1L11 7.5l2.9-1.4L15 3Z"/>' +
            '<path d="M12 4 5 11m6-2-5 5"/>' +
            '<path d="M3 19c2.5-3 5.5-4 9-4s6.5 1 9 4c-4 2.7-14 2.7-18 0Z"/>' +
            '<path d="M8 18c2-1 6-1 8 0"/>'
        ),
        ore_leviathan: svg(
            '<path d="M3 17c2-5 5-7 9-7s7 2 9 7M2 19c2-1 4-1 6 0s4 1 6 0 4-1 8 0"/>' +
            '<path d="M7 12V8l2 3m3-1V6l2 5m3 1V8l-2 3"/>' +
            '<circle cx="8" cy="15" r="1"/><circle cx="12" cy="13.5" r="1"/><circle cx="16" cy="15" r="1"/>'
        ),

        forage_berries: svg(
            '<path d="M12 21V9m0 5-5-3m5 6 5-4M12 9c-2-1-3-3-3-5 2 0 4 1 4 4 1-2 3-3 5-2-.5 2.5-2.5 4-6 3Z"/>' +
            '<circle cx="7" cy="14" r="2.2"/><circle cx="12" cy="17" r="2.2"/><circle cx="17" cy="15.5" r="2.2"/>'
        ),
        forage_thistle: svg(
            '<path d="M12 21v-9M12 17l-4-2m4 1 4-3"/>' +
            '<path d="m12 3 1.4 2 2.5-1-.3 2.6L18 8l-2.4 1.4.3 2.6-2.5-1-1.4 2-1.4-2-2.5 1 .3-2.6L6 8l2.4-1.4L8.1 4l2.5 1L12 3Z"/>' +
            '<path d="M8 15c-2 0-3 1-3 3 2 0 3-1 3-3Zm8-2c2 0 3 1 3 3-2 0-3-1-3-3Z"/>'
        ),
        forage_mushroom: svg(
            '<path d="M4 12c0-4.5 3.5-8 8-8s8 3.5 8 8H4Z"/>' +
            '<path d="M9 12c.2 3-.5 5.5-2 8h10c-1.5-2.5-2.2-5-2-8M8 8h.1m4-2h.1m4 3h.1"/>'
        ),
        forage_seeds: svg(
            '<path d="M12 21V7m0 4-4-3m4 7 5-4"/>' +
            '<path d="M12 7c-2.5 0-4-1.8-4-4 2.5 0 4 1.5 4 4Zm-4 1c-2.5.2-4 1.8-4 4 2.5 0 4-1.5 4-4Zm9 3c2.5.2 4 1.8 4 4-2.5 0-4-1.5-4-4Z"/>' +
            '<path d="M8 15c-2 0-3.5 1.5-3.5 3.5C7 18.5 8 17 8 15Zm4 1c2.5 0 4 1.7 4 4-2.5 0-4-1.5-4-4Z"/>'
        ),
        forage_crops: svg(
            '<path d="M10 22V5m4 17V8"/>' +
            '<path d="M10 8C7 8 6 6 6 4c3 0 4 2 4 4Zm0 4c-3 0-4-2-4-4 3 0 4 2 4 4Zm0 4c-3 0-4-2-4-4 3 0 4 2 4 4Zm4-5c3 0 4-2 4-4-3 0-4 2-4 4Zm0 4c3 0 4-2 4-4-3 0-4 2-4 4Zm0 4c3 0 4-2 4-4-3 0-4 2-4 4Z"/>'
        ),
        forage_dragonegg: svg(
            '<path d="M7 14c0-5 2-10 5-10s5 5 5 10c0 3-2 5-5 5s-5-2-5-5Z"/>' +
            '<path d="m9.5 10 2-2 2 2-1.5 2 2 2M3 20c3-2 5-2 9 0 4-2 6-2 9 0"/>'
        ),
        forage_blackcore: svg(
            '<path d="m12 3 5 6-2 7H9L7 9l5-6Z"/>' +
            '<path d="m12 3-1 7 4 6m-8-7 4 1 6-1M6 20h12m-9-4-2 4m8-4 2 4"/>'
        ),

        structure_camp: svg(
            '<path d="M3 21V9l2-3 2 3 2-3 2 3v12M11 21V4h8l-2 3 2 3h-8"/>' +
            '<path d="M3 13h8M6 13v8m3-8v8"/>'
        ),
        structure_tarpit: svg(
            '<path d="M3 17c1-3 4-5 9-5s8 2 9 5c-2 4-16 4-18 0Z"/>' +
            '<path d="M7 16c1.5-1 3-1 4.5 0s3 1 5 0"/>' +
            '<circle cx="7" cy="9" r="1.5"/><circle cx="15.5" cy="7" r="2"/><circle cx="19" cy="11" r="1"/>'
        ),
        structure_shipwreck: svg(
            '<path d="M3 17c4 4 14 4 18-2l-3 1-1-9-2 9-4 1-1-12-2 12-5 0Z"/>' +
            '<path d="M4 21c2-1 4-1 6 0s4 1 6 0 3-1 5-.5M8 9h9"/>'
        ),
        structure_ruins: svg(
            '<path d="M4 21V6h4v3h3V5h4v3h5v13H4Z"/>' +
            '<path d="M4 13h5v8m6 0v-6h5M11 5l2-2 2 2M12 11h3"/>' +
            '<path d="M7 6V3"/>'
        ),
        structure_mistlands: svg(
            '<path d="M3 21C4 10 8 4 12 4s8 6 9 17M7 21c.5-7 2.5-11 5-11s4.5 4 5 11"/>' +
            '<path d="M5 13h4m-5 4h4m7-4h4m-3 4h4M12 4v6"/>'
        ),
        structure_runestone: svg(
            '<path d="M6 21 7 6l5-3 5 3 1 15H6Z"/>' +
            '<path d="M10 17V8l4 3-4 2 5 4M6 21h12"/>'
        ),

        ship: svg(
            '<path d="M3 14h18l-3 5H7l-4-5ZM12 14V4m0 2 6 6h-6M12 7 7 12h5"/>' +
            '<circle cx="8" cy="16" r="1.3"/><circle cx="12" cy="16" r="1.3"/><circle cx="16" cy="16" r="1.3"/>' +
            '<path d="M2 21c2-1 4-1 6 0s4 1 6 0 4-1 8 0"/>'
        ),
        cart: svg(
            '<path d="M3 6h3l2 10h10l2-7H7M9 9h10M12 9v7"/>' +
            '<circle cx="9" cy="19" r="2"/><circle cx="18" cy="19" r="2"/>'
        ),
        portal: svg(
            '<path d="M5 21V11a7 7 0 0 1 14 0v10h-4V11a3 3 0 0 0-6 0v10H5Z"/>' +
            '<path d="M5 15h4m6 0h4M6.5 7.5l3 2m8-2-3 2M12 4v4"/>' +
            '<path d="M11 13h2l-1 4 2 2"/>'
        ),
        ward: svg(
            '<path d="M7 21 8.2 7 12 3l3.8 4L17 21H7Z"/>' +
            '<path d="M10 17V9l4 3-4 3m4-6v8M7 21h10"/>' +
            '<path d="M5 8 2.5 6.5M4.5 13H2m3.5 4L3 19m16-11 2.5-1.5M19.5 13H22m-3.5 4 2.5 2"/>'
        ),
        bed: svg(
            '<path d="M3 20V7m18 13V8M3 17h18M5 17v3m14-3v3"/>' +
            '<path d="M4 11h16v6H4v-6Z"/>' +
            '<path d="M5 11V8h3.5c1.7 0 3 1.3 3 3M5 14h15"/>'
        ),
        tombstone: svg(
            '<path d="M6 21V9a6 6 0 0 1 12 0v12H6Z"/>' +
            '<path d="M9 10h6m-3-3v10m-2-3 2 3 2-3M4 21h16"/>'
        ),
        player: svg(
            '<path d="m12 2 3.2 6.8L22 12l-6.8 3.2L12 22l-3.2-6.8L2 12l6.8-3.2L12 2Z"/>' +
            '<path d="m8.8 8.8 3.2 1.8 3.2-1.8L12 17l-3.2-8.2Z"/>' +
            '<path d="M10 7 8 5m6 2 2-2"/>'
        ),
        pin: svg(
            '<path d="M7 21V3M7 4h11l-2.5 4L18 12H7"/>' +
            '<path d="m5 21 2-3 2 3"/>'
        ),
        pin_checked: svg(
            '<path d="M7 21V3M7 4h11l-2.5 4L18 12H7"/>' +
            '<path d="m5 21 2-3 2 3M9.5 8l2 2 3.5-4"/>'
        )
    };

    window.VO_ICON_FOR_POI = function (record) {
        var group = record && typeof record.group === "string"
            ? record.group.trim().toLowerCase()
            : "misc";
        var name = record && typeof record.name === "string"
            ? record.name.replace(/[^a-z0-9]/gi, "").toLowerCase()
            : "";

        if (name.indexOf("placeofmystery") !== -1) {
            return "boss_fader";
        }
        if (group !== "boss") {
            return Object.prototype.hasOwnProperty.call(window.VO_ICONS, group)
                ? group
                : "misc";
        }
        if (name.indexOf("eikthyrnir") !== -1) {
            return "boss_eikthyr";
        }
        if (name.indexOf("gdking") !== -1) {
            return "boss_elder";
        }
        if (name.indexOf("bonemass") !== -1) {
            return "boss_bonemass";
        }
        if (name.indexOf("dragonqueen") !== -1) {
            return "boss_moder";
        }
        if (name.indexOf("goblinking") !== -1) {
            return "boss_yagluth";
        }
        if (name.indexOf("dvergrbossentrance") !== -1) {
            return "boss_queen";
        }
        if (name.indexOf("faderlocation") !== -1) {
            return "boss_fader";
        }
        return "boss";
    };
}());
