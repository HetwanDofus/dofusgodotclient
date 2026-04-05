CREATE TABLE IF NOT EXISTS accounts (
    id SERIAL PRIMARY KEY,
    username VARCHAR(30) UNIQUE NOT NULL,
    password VARCHAR(50) NOT NULL,
    pseudo VARCHAR(30) NOT NULL
);

CREATE TABLE IF NOT EXISTS characters (
    id SERIAL PRIMARY KEY,
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    name VARCHAR(30) UNIQUE NOT NULL,
    class SMALLINT NOT NULL,
    sex SMALLINT NOT NULL,
    color1 INTEGER DEFAULT -1,
    color2 INTEGER DEFAULT -1,
    color3 INTEGER DEFAULT -1,
    gfx INTEGER NOT NULL,
    level INTEGER DEFAULT 1,
    map_id INTEGER DEFAULT 8479,
    cell_id INTEGER DEFAULT 314,
    direction SMALLINT DEFAULT 1,

    -- Base stats
    vitality INTEGER DEFAULT 0,
    wisdom INTEGER DEFAULT 0,
    strength INTEGER DEFAULT 0,
    chance INTEGER DEFAULT 0,
    agility INTEGER DEFAULT 0,
    intelligence INTEGER DEFAULT 0,

    -- Combat stats
    hp INTEGER DEFAULT 55,
    max_hp INTEGER DEFAULT 55,
    ap SMALLINT DEFAULT 6,
    mp SMALLINT DEFAULT 3,
    initiative INTEGER DEFAULT 100,
    discernment INTEGER DEFAULT 0,
    range SMALLINT DEFAULT 0,
    summon_limit SMALLINT DEFAULT 1,

    -- Resources
    energy INTEGER DEFAULT 10000,
    max_energy INTEGER DEFAULT 10000,
    bonus_points INTEGER DEFAULT 0,
    bonus_points_spell INTEGER DEFAULT 0,

    -- Experience
    xp BIGINT DEFAULT 0,
    xp_low BIGINT DEFAULT 0,
    xp_high BIGINT DEFAULT 110,
    kama INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS maps (
    id INTEGER PRIMARY KEY,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    superarea INTEGER NOT NULL,
    background INTEGER DEFAULT 0,
    places TEXT DEFAULT '',
    cells JSONB NOT NULL,
    cells_gzip BYTEA NOT NULL,
    walkable_ids INTEGER[] NOT NULL,
    monsters TEXT DEFAULT ''
);

CREATE TABLE IF NOT EXISTS scripted_cells (
    map_id INTEGER NOT NULL,
    cell_id INTEGER NOT NULL,
    action_id INTEGER NOT NULL,
    event_id INTEGER NOT NULL,
    action_args TEXT DEFAULT '',
    conditions TEXT DEFAULT '',
    PRIMARY KEY (map_id, cell_id)
);

CREATE INDEX IF NOT EXISTS idx_scripted_cells_map_id ON scripted_cells(map_id);

CREATE TABLE IF NOT EXISTS item_templates (
    id INTEGER PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    type SMALLINT NOT NULL,
    super_type SMALLINT DEFAULT 0,
    level SMALLINT DEFAULT 1,
    gfx_id INTEGER DEFAULT 0,
    description TEXT DEFAULT '',
    weight INTEGER DEFAULT 1,
    equip_positions JSONB DEFAULT '[]',
    two_handed BOOLEAN DEFAULT false,
    effects JSONB DEFAULT '[]',
    item_set_id INTEGER DEFAULT 0,
    usable BOOLEAN DEFAULT false,
    stackable BOOLEAN DEFAULT true
);

CREATE TABLE IF NOT EXISTS character_items (
    id SERIAL PRIMARY KEY,
    character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    template_id INTEGER NOT NULL REFERENCES item_templates(id),
    quantity INTEGER DEFAULT 1,
    position SMALLINT DEFAULT -1,
    effects JSONB DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS idx_character_items_char_id ON character_items(character_id);
