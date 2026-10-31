# Kometa companion

Wishlistarr can generate a collection file for [Kometa](https://github.com/Kometa-Team/Kometa) so that Plex collections for each kid profile are kept in Kometa. The file only reads the labels Wishlistarr already writes to Plex. Kometa needs no changes and Wishlistarr never calls Kometa or edits its configuration.

## What the file contains

One collection per active kid profile, named after the profile, built as a smart filter on that profile's Plex label:

```yaml
collections:
  Ada:
    smart_filter:
      all:
        label: wishlistarr-ada
  Beau:
    smart_filter:
      all:
        label: wishlistarr-beau
```

Collections are sorted by profile name. Archived profiles are left out, so their collections disappear from the file the next time Kometa runs. Labels are quoted wherever YAML would otherwise read them as a boolean, a number or something else, so any label Wishlistarr accepts is written back exactly.

## Download

A parent can download the file from `GET /api/v1/kometa/collections.yml` while signed in, or with the API key in the `X-Api-Key` header (see [authentication](authentication.md)). It is returned as `application/yaml` with the file name `wishlistarr-collections.yml`. Kids and signed-out callers are refused.

```
curl -H "X-Api-Key: <api key>" -o wishlistarr-collections.yml \
  https://wishlistarr.example/api/v1/kometa/collections.yml
```

Download it again after adding, renaming or archiving a profile.

## Use it from Kometa

Put the file where Kometa can read it, for example in its `config` folder, and list it under the library in Kometa's `config.yml`:

```yaml
libraries:
  Movies:
    collection_files:
      - file: config/wishlistarr-collections.yml
  TV Shows:
    collection_files:
      - file: config/wishlistarr-collections.yml
```

A smart filter collection holds whatever currently carries the label, so it follows the labels Wishlistarr writes and removes.

## Validation

The generated file is checked in the test suite against the collection file JSON schema published by Kometa (release v2.5.3, MIT licence). The vendored copy and its source are in `tests/Wishlistarr.Tests/Kometa`. Newer Kometa releases may add keys but the keys used here are the long-standing smart filter ones.
