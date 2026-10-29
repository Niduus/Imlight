"""Where the Selenopolis boat, doors and dungeon sigils go (SpiralDB has no capture of Selenopolis at all).

The game's zone files never say where a way out leads, but each way in or out stands next to the spot people coming
the other way land on ("Teleport_2_Market" stands by "Target_From_Market"). The table below names each way's
destination zone by hand (from the names) and where it lands: a spot by name, or AUTO, the spot nearest the
destination's own way back (or a step from an object or sigil when there is no spot).

Input: a folder of decoded zone wads (one folder per wad with gamedata.bin.json, triggers.xml.json and volumes.xml.json)
Output: spiraldb-extra/DungeonTeleports JSON (a list of {ZoneName, Teleports}) and a plain report.

    python3 make_selenopolis_teleports.py <decoded wads> <out.json> <report.txt>
"""
import json
import math
import os
import sys

SEL = 'Krokotopia/KT_Selenopolis/'
I = SEL + 'Interiors/'
HUB = 'Krokotopia/KT_Hub'
CITY = SEL + 'KT_Z04_Selenopolis'
MARKET = SEL + 'KT_Z05_Market'
TOMBS = SEL + 'KT_Z06_Forgotten'
GARDEN = SEL + 'KT_Z07_Garden'
AUTO = None

# zone -> [(way out, destination zone, landing spot or AUTO)]
WAYS = {
    HUB: [
        ('KT-ToSELEBoat', CITY, 'Start'),
    ],
    CITY: [
        ('KT-ToOASISBoat', HUB, 'Target_From_Selenopolis'),
        ('Teleport_2_Market', MARKET, 'Start'),
        ('Teleport_2_BlendedGrove', I + 'KT_Z04I01_BlendedGrove', 'Target_From_Selenopolis'),
        ('Teleport_2_Tomb', TOMBS, 'Start'),
        ('Teleport_2_Zigurat_Lower', I + 'KT_Z04_Pyramid', 'Target_From_Sele_Lower'),
        ('Trigger_Teleport_Bypass_2_Lower', CITY, 'Target_Teleport_Bypass_From_Upper'),
        ('Trigger_Teleport_Bypass_2_Upper', CITY, 'Target_Teleport_Bypass_From_Lower'),
        ('Teleport_2_Garden', GARDEN, 'Start'),
        ('Trigger_Teleport_FromTop', CITY, 'Target_Teleport_FromTop'),
        ('Teleport_2_Cere’sa’s House', I + 'KT_Z04I06_HouseCeresa', 'Start'),
        ('Teleport_2_QuasarHouse', I + 'KT_Z04I06_HouseQuasar', 'Start'),
        ('Teleport_2_SafeHouse', I + 'KT_Z04_SafeHouse', 'Target_From_Sele'),
        ('Teleport_2_Sanctum', I + 'KT_Z04_Sanctum', 'Target_From_Sele'),
        ('Teleport_2_MoonFestival', I + 'KT_Z04_MoonFestival', 'Target_From_Sele'),
        ('Teleport_2_SELE_Pyramid', I + 'KT_Z04_Pyramid', 'Start'),
    ],
    I + 'KT_Z04I01_BlendedGrove': [
        ('Teleport_2_Selenopolis', CITY, 'Target_From_BlendedGrove'),
        ('Trigger_Teleport_WheelTutorial', I + 'KT_Z04_Tutorial_Wheel', 'Target_From_Sele'),
        ('Teleport_2_FreezersBattleground', I + 'KT_Z04_FreezersBattleground', 'Target_From_BlendedGrove'),
    ],
    I + 'KT_Z04_Tutorial_Wheel': [('Teleport_2_Sele', I + 'KT_Z04I01_BlendedGrove', 'Target_From_WheelTutorial')],
    I + 'KT_Z04_FreezersBattleground': [('Teleport_2_Sele', I + 'KT_Z04I01_BlendedGrove', 'Target_From_JuliusFreezerBattle')],
    I + 'KT_Z04I06_HouseCeresa': [('Teleport location', CITY, 'Target location-FromCere’sa’s House')],
    I + 'KT_Z04I06_HouseQuasar': [('Teleport location', CITY, 'Target location-FromQuasarHouse')],
    I + 'KT_Z04_MoonFestival': [('Teleport_2_Sele', CITY, 'Target_From_MoonFestival')],
    I + 'KT_Z04_SafeHouse': [('Teleport_2_Sele', CITY, 'Target_From_SafeHouse')],
    I + 'KT_Z04_Sanctum': [
        ('Teleport_2_Sele', CITY, 'Target_From_Sanctum'),
        ('Teleport_2_Sanctum2', CITY, 'Target_From_Sanctum2'),
    ],
    I + 'KT_Z04_Sanctum_Adv': [
        ('Teleport_2_Sele', CITY, 'Target_From_Sanctum'),
        ('Teleport_2_Sanctum2', CITY, 'Target_From_Sanctum2'),
    ],
    I + 'KT_Z04_Pyramid': [
        ('Teleport_2_Sele_Lower', CITY, 'Target_From_Zigurat_Lower'),
        ('Teleport_2_Sele_Upper', CITY, 'Target_From_Zigurat_Upper'),
    ],
    I + 'KT_Z04_Pyramid_Adv': [
        ('Teleport_2_Sele_Lower', CITY, 'Target_From_Zigurat_Lower'),
        ('Teleport_2_Sele_Upper', CITY, 'Target_From_Zigurat_Upper'),
    ],
    MARKET: [
        ('Teleport_2_Selenopolis', CITY, 'Target_From_Market'),
        ('Teleport_Source_To_UndergroundMarket', I + 'KT_Z05I08_Storage_A_Int', 'Start'),
        ('Teleport_Source_To_GrandExposition', I + 'KT_Z05I09_Storage_B_Int', 'Start'),
        ('Trigger_Teleport_To_RogueTheater_Soloi_01', I + 'KT_Z05I06_MainTheater', 'Start'),
        ('Teleport_To_RogueTheater_Multi_01', I + 'KT_Z05I06_MainTheater_Group', 'Start'),
        ('Teleport_To_MARK_PrologueHall_01', I + 'KT_Z05I01_PrologueHall', 'Start'),
        ('Teleport_2_CloudK9', I + 'KT_Z05_Cloud_K9', 'Target_From_Market'),
        ('Teleport_to_MARK_StoryTellers_Den_01', I + 'KT_Z05_StoryTellersDen', 'Start'),
        ('Sigil_LegendOfSutekh', I + 'KT_Z06I11_LegendOfSutekh_Int', 'Start'),
    ],
    I + 'KT_Z05I01_PrologueHall': [
        ('TeleportSource_To_Marketplace_01', MARKET, 'TargetLocation_From_PrologueHall_01'),
        ('TeleportSource_To_Marketplace_02', MARKET, 'TargetLocation_From_PrologueHall_01'),
    ],
    I + 'KT_Z05I06_MainTheater': [('TeleportSource_To_MarketPlace', MARKET, 'TargetLocation_From_RogueTheater_Solo_01')]
        + [(f'Trigger_Room_0{n}_from_Lobby', I + 'KT_Z05I06_MainTheater', to) for n, to in
           ((1, 'TargetLocation_Room_01_To'), (2, 'TargetLocation_Room_02_to'), (3, 'TargetLocation_Room_03_to'), (4, 'TargetLocation_Room_04_to'))]
        + [(f'Trigger_Room_0{n}_to_Lobby', I + 'KT_Z05I06_MainTheater', f'TargetLocation_Room_0{n}_from') for n in (1, 2, 3, 4)],
    I + 'KT_Z05I06_MainTheater_Group': [('TeleportSource_To_MarketPlace', MARKET, 'TargetLocation_From_RogueTheater_Multi_01')]
        + [(f'Trigger_Room_0{n}_from_Lobby', I + 'KT_Z05I06_MainTheater_Group', to) for n, to in
           ((1, 'TargetLocation_Room_01_To'), (2, 'TargetLocation_Room_02_to'), (3, 'TargetLocation_Room_03_to'), (4, 'TargetLocation_Room_04_to'))]
        + [(f'Trigger_Room_0{n}_to_Lobby', I + 'KT_Z05I06_MainTheater_Group', f'TargetLocation_Room_0{n}_from') for n in (1, 2, 3, 4)],
    I + 'KT_Z05_Cloud_K9': [('Teleport source', MARKET, AUTO)],
    I + 'KT_Z05_StoryTellersDen': [('Teleport_To_Market', MARKET, 'Target_From_StorytellersDen')],
    I + 'KT_Z06I11_LegendOfSutekh_Int': [
        ('Teleport Djeserit', MARKET, 'ReturnFromSutekh'),
        ('Teleport Room2', I + 'KT_Z06I11_LegendOfSutekh02_Int', 'Start'),
    ],
    I + 'KT_Z06I11_LegendOfSutekh02_Int': [
        ('Teleport Room1', I + 'KT_Z06I11_LegendOfSutekh_Int', 'FromRoom2'),
        ('Teleport Room3', I + 'KT_Z06I11_LegendOfSutekh03_Int', 'Start'),
    ],
    I + 'KT_Z06I11_LegendOfSutekh03_Int': [
        ('Teleport source (1)', I + 'KT_Z06I11_LegendOfSutekh02_Int', 'From Room3'),
        ('Teleport source', MARKET, 'ReturnFromSutekh'),
    ],
    I + 'KT_Z05I08_Storage_A_Int': [
        ('Teleport_Source_To_MarketPlaceOfIdeas', MARKET, 'TargetLocation_From_UndergroundMarket'),
        ('Teleport_To_MARK_PropDepartment_01', I + 'KT_Z05I03_PropDepartment', 'Start'),
        ('Teleport_To_MARK_Moonseum', I + 'KT_Z05I05_Museum', 'Start'),
        ('Teleport_To_MARK_ShrineOfMarcus_01', I + 'KT_Z05I10_ShrineOfMarcus', 'Start'),
        ('KT-SE-MARK_Teleporter_To_Punderdome', I + 'KT_Z05I11_Punderdome', 'Start'),
    ],
    I + 'KT_Z05I03_PropDepartment': [
        ('TeleportSource_To_UndergroundMarket_01', I + 'KT_Z05I08_Storage_A_Int', 'TargetLocation_From_PropDepartment'),
        ('TeleportSource_To_UndergroundMarket_02', I + 'KT_Z05I08_Storage_A_Int', 'TargetLocation_From_PropDepartment'),
    ],
    I + 'KT_Z05I05_Museum': [
        ('TeleportSource_To_UndergroundMarket', I + 'KT_Z05I08_Storage_A_Int', 'TargetLocation_From_Moonseum'),
        ('Trigger_Exit_Teleporter', I + 'KT_Z05I08_Storage_A_Int', 'TargetLocation_From_Moonseum'),
    ],
    I + 'KT_Z05I10_ShrineOfMarcus': [('TeleportSource_To_UndergroundMarket', I + 'KT_Z05I08_Storage_A_Int', 'TargetLocation_From_ShrineOfMarcus')],
    I + 'KT_Z05I11_Punderdome': [('TeleportSource_To_Market', I + 'KT_Z05I08_Storage_A_Int', AUTO)],
    I + 'KT_Z05I09_Storage_B_Int': [
        ('TeleportSource_To_Market Place of Ideas_01', MARKET, 'TargetLocation_From_GrandExposition'),
        ('Trigger_Enter_PetZone_01', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_Enter_PetZone_01'),
        ('Trigger_Exit_PetZone_01', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_Exit_PetZone_01'),
        ('Teleport_To_MARK_TheaterOfWar_01', I + 'KT_Z05I02_TheaterOfWar', 'Start'),
        ('Teleport_To_MARK_LieBrary_01', I + 'KT_Z05I04_LieBrary', 'Start'),
        ('Teleport_To_MARK_Gatehouse_01', I + 'KT_Z05I07_Gatehouse', 'Start'),
    ],
    I + 'KT_Z05I02_TheaterOfWar': [
        ('TeleportSource_To_GrandExposition_01', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_From_TheaterOfWar_01'),
        ('TeleportSource_To_GrandExposition_02', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_From_TheaterOfWar_01'),
    ],
    I + 'KT_Z05I04_LieBrary': [('TeleportSource_To_GrandExposition', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_From_LieBrary_01')]
        + [(f'Trigger_Teleport_{c}_01', I + 'KT_Z05I04_LieBrary', f'TargetLocation_{c}_01') for c in ('White', 'Yellow', 'Cyan', 'Magenta')]
        + [(f'TeleportSource_From_{c}_01', I + 'KT_Z05I04_LieBrary', 'TargetLocation_Lobby_01') for c in ('White', 'Yellow', 'Cyan', 'Magenta')],
    I + 'KT_Z05I07_Gatehouse': [('TeleportSource_To_GrandExposition', I + 'KT_Z05I09_Storage_B_Int', 'TargetLocation_From_Gatehouse_01')],
    TOMBS: [
        ('To The Hub', CITY, 'Target_From_Forgotten'),
        ('TP Hall 01 to Hall 02', TOMBS, 'Into Hall 02'),
        ('Trigger KrokosphinxDeathSt', I + 'KT_KrokosphinxBoss', 'Start'),
        ('Trigger_Teleport_01_A', TOMBS, 'Exit Callisto to Hall 02'),
        ('Trigger_Teleport_01_B', TOMBS, 'Exit Callisto Back to Hall 01'),
        ('Trigger_Teleport_02_A', TOMBS, 'Land Hall 03 from 02'),
        ('Trigger_Teleport_02_B', TOMBS, 'Land Hall 02 from 03'),
        ('Trigger_Teleport_03_A', TOMBS, 'Into Hall 02'),
        ('Trigger_Teleport_03_B', TOMBS, 'Into Hall 04'),
        ('Sigil ChamberOfPhoebe', I + 'KT_Z06I09_ChamberPhoebe_Int', 'Start'),
        ('Sigil Chamber of Almathea', I + 'KT_Z06I08_ChamberAmalthea_Int', 'Start'),
        ('Sigil Chamber of Callisto', I + 'KT_Z06I06_ChamberCallisto_New_Int', 'Start'),
        ('Sigil Chamber of Mimas', I + 'KT_Z06I07_ChamberMimas_Int', 'Start'),
        ('Sigil Chamber of Light', I + 'KT_Z06I02_TombLight_Int', 'Start'),
        ('Sigil Chamber of Night', I + 'KT_Z06I03_TombNight_Int', 'Start'),
        ('Sigil Chamber of Darkness', I + 'KT_Z06I10_DarkChamber_Int', 'Start'),
        ('Sigil ChamberOfTethys', I + 'KT_Z06I05_ChamberTethys_Int', 'Start'),
        ('Teleport to Crypt Of Ra', I + 'KT_Z06I12_CryptOfRa_Int', 'Start'),
        ('Sigil Field of Reeds', I + 'KT_Z06I04_FieldReeds_Int', 'Start'),
        # The press-X stones by the entrance and their partners deep in the halls (TargetLocation_01 is by the entrance).
        ('KT-Teleporter_ForgottenTombs_01', TOMBS, 'TargetLocation_02'),
        ('KT-Teleporter_ForgottenTombs_02', TOMBS, 'TargetLocation_03'),
        ('KT-Teleporter_ForgottenTombs_03', TOMBS, 'TargetLocation_04'),
        ('KT-Teleporter_ForgottenTombs_01_B', TOMBS, 'TargetLocation_01'),
        ('KT-Teleporter_ForgottenTombs_02_B', TOMBS, 'TargetLocation_01'),
        ('KT-Teleporter_ForgottenTombs_03_B', TOMBS, 'TargetLocation_01'),
    ],
    I + 'KT_KrokosphinxBoss': [('Teleport source', TOMBS, 'Return Krokosphinx')],
    I + 'KT_Z06I09_ChamberPhoebe_Int': [('Return TombOfForgottenFromPhoebe', TOMBS, 'Exit ChamberOfPhoebe')],
    I + 'KT_Z06I08_ChamberAmalthea_Int': [('TP Return From Amalthea', TOMBS, 'Exit ChamberOfAlmathea')],
    I + 'KT_Z06I06_ChamberCallisto_New_Int': [
        ('Return Hall 01 From Callisto', TOMBS, 'Exit Callisto Back to Hall 01'),
        ('Into Hall 02 From Callisto', TOMBS, 'Exit Callisto to Hall 02'),
    ],
    I + 'KT_Z06I07_ChamberMimas_Int': [('Teleport source', TOMBS, 'Exit ChamberOfMimas')],
    I + 'KT_Z06I02_TombLight_Int': [('Teleport source', TOMBS, 'Exit ChamberOfLight')],
    I + 'KT_Z06I03_TombNight_Int': [('Teleport source', TOMBS, 'Exit ChamberOfNight')],
    I + 'KT_Z06I10_DarkChamber_Int': [
        ('Teleport BackToHall03', TOMBS, 'Into Hall 02'),
        ('Teleport IntoHall04', TOMBS, 'Into Hall 04'),
    ],
    I + 'KT_Z06I05_ChamberTethys_Int': [('Teleport source', TOMBS, 'Exit ChamberOfTethys')],
    I + 'KT_Z06I04_FieldReeds_Int': [('TP to TombOfForgottenFromFieldReeds', TOMBS, 'Field of Reeds')],
    I + 'KT_Z06I12_CryptOfRa_Int': [
        ('Teleport To TombOfForgotten', TOMBS, 'From Crypt Of Ra'),
        ('Teleport To TombOfForgotten (1)', TOMBS, 'From Crypt Of Ra'),
        ('Teleport To MotherSunnRoom', I + 'KT_Z06I12_CryptOfRa_Int', 'MotherSunnRoom'),
        ('Teleport To CryptRoom01', I + 'KT_Z06I12_CryptOfRa_Int', 'From MotherSunn'),
    ],
    I + 'KT_Z06I12_CryptOfRa_Int_Hard': [
        ('Teleport To TombOfForgotten', TOMBS, 'From Crypt Of Ra'),
        ('Teleport To TombOfForgotten (1)', TOMBS, 'From Crypt Of Ra'),
    ],
    I + 'KT_Z06I01_ForgottenRooms': [
        ('TP Exit to Hall 03 from Phoebe', TOMBS, 'Exit ChamberOfPhoebe'),
        ('TP Exit to Hall 03 from Night', TOMBS, 'Exit ChamberOfNight'),
        ('TP Exit to Hall 03 From Light', TOMBS, 'Exit ChamberOfLight'),
    ],
    GARDEN: [
        ('Teleport source', CITY, 'Target_From_Garden'),
        ('Teleport source-ToGutter', I + 'KT_Z07I02_Gutter', 'Start'),
        ('Trigger-TeleportToVivarium', I + 'KT_Z07I01_Vivarium', 'Start'),
        ('Teleport to Interior sigil-MoonShrine', I + 'KT_Z07I04_MoonShrine', 'Start'),
    ],
    I + 'KT_Z07I04_MoonShrine': [('Teleport source-TeleportToGarden', GARDEN, 'Target location-ShrineNewMoon')],
    I + 'KT_Z07I01_Vivarium': [
        ('Teleport source-ToGarden', GARDEN, 'Target location-FromVivarium'),
        ('Trigger_Teleport_PitofGorn', I + 'KT_Z07_PitOfGorn', 'Target_From_Vivarium'),
        ('Teleport to Interior sigil-DarkAbode', I + 'KT_Z07I03_DarkAbode', 'Start'),
    ],
    I + 'KT_Z07_PitOfGorn': [('Teleport source', I + 'KT_Z07I01_Vivarium', 'Target-FromPitOfGorn')],
    I + 'KT_Z07I03_DarkAbode': [('Teleport source', I + 'KT_Z07I01_Vivarium', 'Target location-FromDarkAbode')],
    I + 'KT_Z07I03_DarkAbode_Hard': [('Teleport source', I + 'KT_Z07I01_Vivarium', 'Target location-FromDarkAbode')],
    I + 'KT_Z07I02_Gutter': [
        ('Teleport source-ToGarden', GARDEN, 'Target location-FromGutter'),
        ('Teleport source', GARDEN, 'Target location-PublicBoss'),
        ('Trigger-BrightsLab', I + 'KT_Z07_BrightsLab', 'Start'),
        ('Teleport to Interior sigil-1MBoss', I + 'KT_Z07_Unknown', 'Start'),
        ('Teleport to Interior sigil-SecretBase', I + 'KT_Z07I05_SecretBase', 'Start'),
        ('Teleport to Interior sigil-Aqueduct', I + 'KT_Z07_Aqueduct', 'Start'),
        ('Teleport to Interior sigil-TrialsOfTreebow', I + 'KT_Z07_TrialsOfTreebow_01', 'Start'),
        ('Teleport_To_ChamberOfLegends', I + 'KT_Z07I06_ChamberOfLegends', 'Start'),
        ('Teleport_2_MemoriesofGalkoth', I + 'KT_Z07_MemoryofGalkoth_01', 'Start'),
    ],
    I + 'KT_Z07_BrightsLab': [('Teleport source', I + 'KT_Z07I02_Gutter', 'Target location-FromBrightsLab')],
    I + 'KT_Z07_Unknown': [('Teleport source', I + 'KT_Z07I02_Gutter', 'Target location-Unknown')],
    I + 'KT_Z07I05_SecretBase': [('Teleport source', I + 'KT_Z07I02_Gutter', 'Target location-FromSecretBase')],
    I + 'KT_Z07_Aqueduct': [('Teleport source', I + 'KT_Z07I02_Gutter', 'Target location-Aqueduct')],
    I + 'KT_Z07_TrialsOfTreebow_01': [
        ('Teleport source-ToGutter', I + 'KT_Z07I02_Gutter', 'Target location-FromTreebowTrials'),
        ('Teleport source-ToRoom2', I + 'KT_Z07_TrialsOfTreebow_02', 'Start'),
    ],
    I + 'KT_Z07_TrialsOfTreebow_02': [
        ('Teleport source-To1', I + 'KT_Z07_TrialsOfTreebow_01', 'Target location-FromRoom2'),
        ('Teleport source-To3', I + 'KT_Z07_TrialsOfTreebow_03', 'Start'),
    ],
    I + 'KT_Z07_TrialsOfTreebow_03': [('Teleport source-ToGutter', I + 'KT_Z07I02_Gutter', 'Target location-FromTreebowTrials')],
    I + 'KT_Z07I06_ChamberOfLegends': [
        ('TeleportSource_To_Garden', I + 'KT_Z07I02_Gutter', 'TargetLocation_From_ChamberOfLegends'),
        ('TeleportSource_To_Room_02', I + 'KT_Z07I06_ChamberOfLegends_02', 'Start'),
    ],
    I + 'KT_Z07I06_ChamberOfLegends_02': [
        ('TeleportSource_To_Room_01', I + 'KT_Z07I06_ChamberOfLegends', 'TargetLocation_From_Room_02'),
        ('TeleportSource_To_Room_03', I + 'KT_Z07I06_ChamberOfLegends_03', 'Start'),
    ],
    I + 'KT_Z07I06_ChamberOfLegends_03': [
        ('Teleport source', I + 'KT_Z07I06_ChamberOfLegends_02', 'TargetLocation_From_Room_03'),
        ('Trigger_Exit_Chamber', I + 'KT_Z07I02_Gutter', 'TargetLocation_From_ChamberOfLegends'),
    ],
    I + 'KT_Z07_MemoryofGalkoth_01': [
        ('Teleport source', I + 'KT_Z07I02_Gutter', 'Target_From_MemoryofGalkoth'),
        ('Trigger_Teleport_R2', I + 'KT_Z07_MemoryofGalkoth_02', 'Target_From_Room1'),
    ],
    I + 'KT_Z07_MemoryofGalkoth_02': [
        ('Teleport_2_Room1', I + 'KT_Z07_MemoryofGalkoth_01', 'Target_From_R2'),
        ('Trigger_Teleport_2_Room3', I + 'KT_Z07_MemoryofGalkoth_03', 'Target_From_Room2'),
    ],
    I + 'KT_Z07_MemoryofGalkoth_03': [
        ('Teleport_2_Room2', I + 'KT_Z07_MemoryofGalkoth_02', 'Target_From_Room3'),
        ('Trigger_Teleport_Exit', I + 'KT_Z07I02_Gutter', 'Target_From_MemoryofGalkoth'),
    ],
}

# How far from an object or sigil a wizard lands when there is no spot to land on, and where nothing is said.
STEP = 250.0


def walk(node, cls, out):
    if isinstance(node, dict):
        if node.get('$class') == cls:
            out.append(node)
        for value in node.values():
            walk(value, cls, out)
    elif isinstance(node, list):
        for value in node:
            walk(value, cls, out)
    return out


class Zone:
    def __init__(self, folder):
        self.data = json.load(open(os.path.join(folder, 'gamedata.bin.json'), encoding='utf-8'))
        self.name = self.data['m_zoneName']
        self.locations = {l['m_locName']: (l['m_location']['X'], l['m_location']['Y'], l['m_location']['Z'])
                          for l in reversed(self.data.get('m_locationList') or [])}
        self.objects = {o['m_zoneTag']: (o['m_location']['X'], o['m_location']['Y'], o['m_location']['Z'])
                        for o in self.data.get('m_objectList') or [] if o and o.get('m_zoneTag')}
        self.sigils = {s['m_zoneTag2']: (s['m_location']['X'], s['m_location']['Y'], s['m_location']['Z'])
                       for s in walk(self.data, 'MinigameSigilInfo', [])}
        events = {}
        path = os.path.join(folder, 'volumes.xml.json')
        if os.path.exists(path):
            for volume in json.load(open(path, encoding='utf-8')).get('m_volumes') or []:
                for event in volume.get('m_enterEvents') or []:
                    events[event] = (volume['m_locationX'], volume['m_locationY'], volume['m_locationZ'])
        self.ways = {}
        path = os.path.join(folder, 'triggers.xml.json')
        for trigger in (json.load(open(path, encoding='utf-8')).get('m_triggers') or []) if os.path.exists(path) else []:
            if walk(trigger.get('m_results'), 'ResTeleport', []):
                self.ways[trigger['m_triggerName']] = next(
                    (events[e] for e in trigger.get('m_fireEvents') or [] if e in events), None)
        self.ways.update(self.sigils)
        for tag, position in self.objects.items():
            self.ways.setdefault(tag.removesuffix(' instance'), position)

    def step_from(self, position):
        """A spot a step from an object toward the nearest named spot, clear of the object."""
        target = min(self.locations.values(), key=lambda p: math.dist(p, position))
        dx, dy = target[0] - position[0], target[1] - position[1]
        length = max(math.hypot(dx, dy), 0.001)
        step = min(STEP, length)
        yaw = math.atan2(-dx, -dy)
        return f'{position[0] + dx / length * step:.3f},{position[1] + dy / length * step:.3f},{position[2]:.3f},{yaw:.6f}'


def main():
    decoded, out_path, report_path = sys.argv[1:4]
    zones = {}
    for name in os.listdir(decoded):
        if os.path.exists(os.path.join(decoded, name, 'gamedata.bin.json')):
            zone = Zone(os.path.join(decoded, name))
            zones[zone.name] = zone

    report = []
    out = []
    for zone_name, ways in WAYS.items():
        zone = zones[zone_name]
        teleports = []
        for way, dest_name, landing in ways:
            if way not in zone.ways:
                report.append(f'{zone_name}: no way named {way!r} in the game files')
                continue
            dest = zones.get(dest_name)
            if dest is None:
                report.append(f'{zone_name}: {way!r} leads to {dest_name}, which is not decoded')
                continue
            if landing is AUTO:
                back = [p for w, d, _ in WAYS.get(dest_name, []) if d == zone_name for p in [dest.ways.get(w)] if p]
                if not back:
                    report.append(f'{zone_name}: {way!r} has no way back in {dest_name} to land by')
                    continue
                spot = min(dest.locations.items(), key=lambda kv: math.dist(kv[1], back[0]))
                landing = spot[0] if math.dist(spot[1], back[0]) < 900 else dest.step_from(back[0])
            elif landing not in dest.locations:
                report.append(f'{zone_name}: {way!r} lands on {landing!r}, which {dest_name} does not have')
                continue
            teleports.append({
                'TriggerName': way,
                'Teleport': {
                    'm_exitTeleporter': 0,
                    'm_teleporterTag': 0,
                    'm_teleportType': 'TELEPORT_STATIC',
                    'm_transitionID': 0,
                    'm_destinationLoc': landing,
                    'm_destinationZone': dest_name,
                },
            })
            report.append(f'{zone_name}: {way} -> {dest_name} @ {landing}')
        out.append({'ZoneName': zone_name, 'Teleports': teleports})

    # Ways out the table leaves alone, so they show up in the report.
    for zone_name, zone in sorted(zones.items()):
        if not zone_name.startswith(SEL):
            continue
        listed = {w for w, _, _ in WAYS.get(zone_name, [])}
        for way in sorted(set(zone.ways) - listed):
            if way in zone.sigils or way in zone.ways and zone.ways[way] is not None and way not in zone.objects \
                    and way + ' instance' not in zone.objects:
                report.append(f'{zone_name}: {way!r} left out (no known destination)')

    json.dump(out, open(out_path, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
    open(report_path, 'w', encoding='utf-8').write('\n'.join(report) + '\n')
    print(sum(len(z['Teleports']) for z in out), 'teleports;', sum('left out' in r or 'no way' in r or 'not' in r for r in report), 'notes')


if __name__ == '__main__':
    main()
