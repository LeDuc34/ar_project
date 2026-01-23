using System;
using System.Collections;
using UnityEngine;
using GeoscaleCadastre.Models;

namespace GeoscaleCadastre.Map
{
    /// <summary>
    /// Gestionnaire de carte pour Mapbox Unity SDK
    /// Implémente les animations et comportements de Geoscale (flyTo, auto-zoom)
    /// </summary>
    public class MapManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField]
        [Tooltip("Référence au composant AbstractMap de Mapbox")]
        private MonoBehaviour _mapboxMap; // AbstractMap - utiliser MonoBehaviour pour permettre l'assignation dans l'Inspector

        [SerializeField]
        [Tooltip("Latitude par défaut (Paris)")]
        private double _defaultLatitude = 48.8566;

        [SerializeField]
        [Tooltip("Longitude par défaut (Paris)")]
        private double _defaultLongitude = 2.3522;

        [SerializeField]
        [Tooltip("Zoom par défaut")]
        private float _defaultZoom = 15f;

        [Header("Animation")]
        [SerializeField]
        [Tooltip("Durée par défaut des animations flyTo (secondes)")]
        private float _defaultFlyDuration = 1.2f;

        [SerializeField]
        [Tooltip("Courbe d'animation")]
        private AnimationCurve _flyCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        // État actuel
        private double _currentLatitude;
        private double _currentLongitude;
        private float _currentZoom;
        private Coroutine _flyCoroutine;

        // Events
        public event Action OnMapInitialized;
        public event Action<double, double, float> OnMapMoved;
        public event Action OnFlyToStarted;
        public event Action OnFlyToCompleted;

        /// <summary>Position actuelle (latitude)</summary>
        public double CurrentLatitude { get { return _currentLatitude; } }

        /// <summary>Position actuelle (longitude)</summary>
        public double CurrentLongitude { get { return _currentLongitude; } }

        /// <summary>Zoom actuel</summary>
        public float CurrentZoom { get { return _currentZoom; } }

        private void Start()
        {
            // === DEBUG: Vérification des références au démarrage ===
            Debug.Log("=== [MapManager] DEBUG START ===");
            Debug.Log(string.Format("[MapManager] _mapboxMap: {0}", _mapboxMap != null ? _mapboxMap.GetType().Name : "NULL"));
            Debug.Log(string.Format("[MapManager] Default position: ({0}, {1}) zoom {2}", _defaultLatitude, _defaultLongitude, _defaultZoom));
            Debug.Log("=== [MapManager] DEBUG END ===");

            Initialize();
        }

        /// <summary>
        /// Initialise la carte avec les coordonnées par défaut
        /// </summary>
        public void Initialize()
        {
            Initialize(_defaultLatitude, _defaultLongitude, _defaultZoom);
        }

        /// <summary>
        /// Initialise la carte avec des coordonnées spécifiques
        /// </summary>
        public void Initialize(double latitude, double longitude, float zoom)
        {
            _currentLatitude = latitude;
            _currentLongitude = longitude;
            _currentZoom = zoom;

            // Initialiser Mapbox si disponible
            if (_mapboxMap != null)
            {
                // Appeler la méthode Initialize de Mapbox via reflection ou interface
                // Note: Dépend de la version du SDK Mapbox installé
                InitializeMapboxMap(latitude, longitude, (int)zoom);
            }

            if (OnMapInitialized != null)
                OnMapInitialized();

            Debug.Log(string.Format("[MapManager] Carte initialisée: ({0}, {1}) zoom {2}",
                latitude, longitude, zoom));
        }

        /// <summary>
        /// Animation fluide vers une position (pattern Geoscale flyTo)
        /// </summary>
        /// <param name="latitude">Latitude cible</param>
        /// <param name="longitude">Longitude cible</param>
        /// <param name="zoom">Zoom cible</param>
        /// <param name="duration">Durée de l'animation en secondes</param>
        public void FlyTo(double latitude, double longitude, float zoom, float duration = -1)
        {
            if (duration < 0) duration = _defaultFlyDuration;

            // Arrêter l'animation précédente si elle existe
            if (_flyCoroutine != null)
            {
                StopCoroutine(_flyCoroutine);
            }

            _flyCoroutine = StartCoroutine(FlyToCoroutine(latitude, longitude, zoom, duration));
        }

        /// <summary>
        /// Centre la carte sur une parcelle avec zoom automatique (pattern Geoscale)
        /// </summary>
        /// <param name="parcel">Parcelle cible</param>
        /// <param name="duration">Durée de l'animation</param>
        public void CenterOnParcel(ParcelModel parcel, float duration = 0.8f)
        {
            if (parcel == null)
            {
                Debug.LogWarning("[MapManager] Parcelle null");
                return;
            }

            // Calculer le zoom optimal basé sur la taille de la parcelle
            float targetZoom = CalculateOptimalZoom(parcel);

            // Utiliser le centroïde de la parcelle
            double lat = parcel.Centroid.y;
            double lng = parcel.Centroid.x;

            Debug.Log(string.Format("[MapManager] Centrage sur parcelle: ({0}, {1}) zoom {2}",
                lat, lng, targetZoom));

            FlyTo(lat, lng, targetZoom, duration);
        }

        /// <summary>
        /// Centre la carte sur une adresse
        /// </summary>
        /// <param name="address">Résultat de recherche d'adresse</param>
        /// <param name="zoom">Zoom cible (défaut: 18)</param>
        /// <param name="duration">Durée de l'animation</param>
        public void CenterOnAddress(AddressResult address, float zoom = 18f, float duration = 2f)
        {
            if (address == null)
            {
                Debug.LogWarning("[MapManager] Adresse null");
                return;
            }

            Debug.Log(string.Format("[MapManager] Navigation vers: {0}", address.Text));
            FlyTo(address.Latitude, address.Longitude, zoom, duration);
        }

        /// <summary>
        /// Déplace la carte instantanément (sans animation)
        /// </summary>
        public void SetPosition(double latitude, double longitude, float zoom)
        {
            Debug.Log(string.Format("[MapManager] >>> SetPosition - lat: {0:F6}, lng: {1:F6}, zoom: {2:F1}",
                latitude, longitude, zoom));

            _currentLatitude = latitude;
            _currentLongitude = longitude;
            _currentZoom = zoom;

            Debug.Log("[MapManager] Appel UpdateMapboxPosition...");
            UpdateMapboxPosition();

            if (OnMapMoved != null)
            {
                Debug.Log("[MapManager] Déclenchement événement OnMapMoved");
                OnMapMoved(latitude, longitude, zoom);
            }
        }

        /// <summary>
        /// Calcule le niveau de zoom optimal basé sur la taille de la parcelle
        /// Pattern identique à Geoscale
        /// </summary>
        private float CalculateOptimalZoom(ParcelModel parcel)
        {
            float maxDimension = parcel.GetMaxDimension();

            // Conversion approximative degrés -> mètres pour la France
            // 1 degré latitude ≈ 111km, 1 degré longitude ≈ 75km (à ~46° latitude)
            float dimensionMeters = maxDimension * 90000f; // Approximation

            // Échelle de zoom (identique à Geoscale)
            if (dimensionMeters > 1000) return 15f;      // Très grande parcelle
            if (dimensionMeters > 500) return 16f;       // Grande
            if (dimensionMeters > 200) return 17f;       // Moyenne
            if (dimensionMeters > 100) return 18f;       // Petite
            return 19f;                                   // Très petite
        }

        private IEnumerator FlyToCoroutine(double targetLat, double targetLng, float targetZoom, float duration)
        {
            if (OnFlyToStarted != null)
                OnFlyToStarted();

            double startLat = _currentLatitude;
            double startLng = _currentLongitude;
            float startZoom = _currentZoom;

            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // Appliquer la courbe d'animation
                float curveT = _flyCurve.Evaluate(t);

                // Interpoler les valeurs
                _currentLatitude = Lerp(startLat, targetLat, curveT);
                _currentLongitude = Lerp(startLng, targetLng, curveT);
                _currentZoom = Mathf.Lerp(startZoom, targetZoom, curveT);

                // Mettre à jour la carte Mapbox
                UpdateMapboxPosition();

                yield return null;
            }

            // S'assurer d'atteindre exactement la cible
            _currentLatitude = targetLat;
            _currentLongitude = targetLng;
            _currentZoom = targetZoom;
            UpdateMapboxPosition();

            _flyCoroutine = null;

            if (OnFlyToCompleted != null)
                OnFlyToCompleted();

            if (OnMapMoved != null)
                OnMapMoved(_currentLatitude, _currentLongitude, _currentZoom);
        }

        private double Lerp(double a, double b, float t)
        {
            return a + (b - a) * t;
        }

        private void InitializeMapboxMap(double lat, double lng, int zoom)
        {
            // Initialisation Mapbox via reflection (pour compatibilité sans dépendance directe)
            if (_mapboxMap == null) return;

            try
            {
                var mapType = _mapboxMap.GetType();

                // Chercher la méthode Initialize(Vector2d, int)
                var initMethod = mapType.GetMethod("Initialize",
                    new Type[] { typeof(object), typeof(int) });

                if (initMethod != null)
                {
                    // Créer Vector2d (lat, lng)
                    var vector2dType = Type.GetType("Mapbox.Utils.Vector2d, Mapbox.Unity");
                    if (vector2dType != null)
                    {
                        var center = Activator.CreateInstance(vector2dType, lat, lng);
                        initMethod.Invoke(_mapboxMap, new object[] { center, zoom });
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(string.Format("[MapManager] Impossible d'initialiser Mapbox: {0}", e.Message));
            }
        }

        private void UpdateMapboxPosition()
        {
            // Mise à jour de la position Mapbox via reflection
            if (_mapboxMap == null)
            {
                Debug.LogWarning("[MapManager] UpdateMapboxPosition: _mapboxMap est NULL, impossible de mettre à jour");
                return;
            }

            try
            {
                var mapType = _mapboxMap.GetType();
                Debug.Log(string.Format("[MapManager] UpdateMapboxPosition - MapType: {0}", mapType.Name));

                // Option 1: Chercher la méthode UpdateMap(Vector2d, float)
                var updateMethod = mapType.GetMethod("UpdateMap");
                if (updateMethod != null)
                {
                    var vector2dType = Type.GetType("Mapbox.Utils.Vector2d, Mapbox.Unity");
                    if (vector2dType != null)
                    {
                        var center = Activator.CreateInstance(vector2dType, _currentLatitude, _currentLongitude);
                        Debug.Log(string.Format("[MapManager] Appel UpdateMap via reflection - center: ({0}, {1}), zoom: {2}",
                            _currentLatitude, _currentLongitude, _currentZoom));
                        updateMethod.Invoke(_mapboxMap, new object[] { center, _currentZoom });
                        Debug.Log("[MapManager] UpdateMap appelé avec succès");
                        return;
                    }
                    else
                    {
                        Debug.LogWarning("[MapManager] Type Vector2d non trouvé");
                    }
                }
                else
                {
                    Debug.Log("[MapManager] Méthode UpdateMap non trouvée, tentative via propriétés");
                }

                // Option 2: Modifier les propriétés directement
                var centerProp = mapType.GetProperty("CenterLatitudeLongitude");
                var zoomProp = mapType.GetProperty("Zoom");

                if (centerProp != null)
                {
                    var vector2dType = Type.GetType("Mapbox.Utils.Vector2d, Mapbox.Unity");
                    if (vector2dType != null)
                    {
                        var center = Activator.CreateInstance(vector2dType, _currentLatitude, _currentLongitude);
                        centerProp.SetValue(_mapboxMap, center);
                        Debug.Log("[MapManager] CenterLatitudeLongitude défini via propriété");
                    }
                }
                else
                {
                    Debug.LogWarning("[MapManager] Propriété CenterLatitudeLongitude non trouvée");
                }

                if (zoomProp != null)
                {
                    zoomProp.SetValue(_mapboxMap, _currentZoom);
                    Debug.Log("[MapManager] Zoom défini via propriété");
                }
                else
                {
                    Debug.LogWarning("[MapManager] Propriété Zoom non trouvée");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(string.Format("[MapManager] Erreur mise à jour Mapbox: {0}", e.Message));
            }
        }

        /// <summary>
        /// Convertit une position monde Unity en coordonnées GPS via Mapbox SDK
        /// Utilise AbstractMap.WorldToGeoPosition pour une conversion précise
        /// </summary>
        /// <param name="worldPos">Position dans le monde Unity</param>
        /// <param name="latitude">Latitude résultante</param>
        /// <param name="longitude">Longitude résultante</param>
        /// <returns>True si la conversion a réussi</returns>
        public bool TryWorldToGeoPosition(Vector3 worldPos, out double latitude, out double longitude)
        {
            Debug.Log(string.Format("[MapManager.TryWorldToGeoPosition] >>> Appel avec worldPos: {0}", worldPos));

            latitude = 0;
            longitude = 0;

            if (_mapboxMap == null)
            {
                Debug.LogError("[MapManager.TryWorldToGeoPosition] ERREUR: AbstractMap non assigné, conversion IMPOSSIBLE");
                return false;
            }

            Debug.Log("[MapManager.TryWorldToGeoPosition] AbstractMap OK, tentative de conversion...");

            try
            {
                var mapType = _mapboxMap.GetType();

                // Chercher la méthode WorldToGeoPosition(Vector3)
                var worldToGeoMethod = mapType.GetMethod("WorldToGeoPosition", new Type[] { typeof(Vector3) });

                if (worldToGeoMethod != null)
                {
                    // Appeler WorldToGeoPosition et récupérer le Vector2d résultant
                    var result = worldToGeoMethod.Invoke(_mapboxMap, new object[] { worldPos });

                    if (result != null)
                    {
                        // Extraire x (latitude) et y (longitude) du Vector2d
                        var resultType = result.GetType();
                        var xField = resultType.GetField("x");
                        var yField = resultType.GetField("y");

                        if (xField != null && yField != null)
                        {
                            latitude = (double)xField.GetValue(result);
                            longitude = (double)yField.GetValue(result);

                            Debug.Log(string.Format("[MapManager] Conversion Mapbox: world({0}) -> GPS({1:F6}, {2:F6})",
                                worldPos, latitude, longitude));

                            return true;
                        }
                    }
                }

                Debug.LogWarning("[MapManager] Méthode WorldToGeoPosition non trouvée");
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format("[MapManager] Erreur conversion WorldToGeo: {0}", e.Message));
            }

            return false;
        }

        /// <summary>
        /// Convertit des coordonnées GPS en position monde Unity via Mapbox SDK
        /// Utilise AbstractMap.GeoToWorldPosition pour une conversion précise
        /// </summary>
        /// <param name="latitude">Latitude</param>
        /// <param name="longitude">Longitude</param>
        /// <param name="worldPos">Position Unity résultante</param>
        /// <returns>True si la conversion a réussi</returns>
        public bool TryGeoToWorldPosition(double latitude, double longitude, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;

            Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] Entrée: lat={0:F6}, lng={1:F6}", latitude, longitude));
            Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] _mapboxMap est null? {0}", _mapboxMap == null));

            if (_mapboxMap == null)
            {
                Debug.LogError("[MapManager.TryGeoToWorldPosition] ERREUR: _mapboxMap (AbstractMap) est NULL! Assignez-le dans l'Inspector.");
                return false;
            }

            Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] _mapboxMap OK: type={0}, name={1}",
                _mapboxMap.GetType().Name, _mapboxMap.name));

            try
            {
                var mapType = _mapboxMap.GetType();

                // Chercher Vector2d dans l'assembly de la map ou dans tous les assemblies chargés
                Type vector2dType = null;

                // Méthode 1: Chercher dans l'assembly de AbstractMap
                var mapAssembly = mapType.Assembly;
                vector2dType = mapAssembly.GetType("Mapbox.Utils.Vector2d");

                // Méthode 2: Si pas trouvé, chercher dans tous les assemblies
                if (vector2dType == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        vector2dType = assembly.GetType("Mapbox.Utils.Vector2d");
                        if (vector2dType != null)
                        {
                            Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] Vector2d trouvé dans assembly: {0}", assembly.GetName().Name));
                            break;
                        }
                    }
                }

                if (vector2dType == null)
                {
                    Debug.LogError("[MapManager.TryGeoToWorldPosition] ERREUR: Type Mapbox.Utils.Vector2d non trouvé dans aucun assembly!");
                    Debug.Log("[MapManager.TryGeoToWorldPosition] Assemblies disponibles:");
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name.ToLower().Contains("mapbox"))
                        {
                            Debug.Log(string.Format("  - {0}", assembly.GetName().Name));
                        }
                    }
                    return false;
                }

                Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] Vector2d type trouvé: {0}", vector2dType.FullName));

                // Créer le Vector2d avec lat/lng
                var latLng = Activator.CreateInstance(vector2dType, latitude, longitude);
                Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] Vector2d créé: {0}", latLng));

                // Chercher la méthode GeoToWorldPosition(Vector2d, bool)
                var geoToWorldMethod = mapType.GetMethod("GeoToWorldPosition",
                    new Type[] { vector2dType, typeof(bool) });

                if (geoToWorldMethod == null)
                {
                    // Essayer sans le paramètre bool
                    geoToWorldMethod = mapType.GetMethod("GeoToWorldPosition",
                        new Type[] { vector2dType });

                    if (geoToWorldMethod != null)
                    {
                        Debug.Log("[MapManager.TryGeoToWorldPosition] Méthode GeoToWorldPosition(Vector2d) trouvée (sans bool)");
                        var result = geoToWorldMethod.Invoke(_mapboxMap, new object[] { latLng });
                        if (result is Vector3)
                        {
                            worldPos = (Vector3)result;
                            Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] SUCCÈS: GPS({0:F6}, {1:F6}) -> world({2})",
                                latitude, longitude, worldPos));
                            return true;
                        }
                    }

                    Debug.LogError("[MapManager.TryGeoToWorldPosition] ERREUR: Méthode GeoToWorldPosition non trouvée sur " + mapType.Name);
                    // Lister les méthodes disponibles pour debug
                    Debug.Log("[MapManager.TryGeoToWorldPosition] Méthodes disponibles:");
                    foreach (var method in mapType.GetMethods())
                    {
                        if (method.Name.Contains("Geo") || method.Name.Contains("World") || method.Name.Contains("Position"))
                        {
                            Debug.Log(string.Format("  - {0}({1})", method.Name,
                                string.Join(", ", System.Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name))));
                        }
                    }
                    return false;
                }

                Debug.Log("[MapManager.TryGeoToWorldPosition] Méthode GeoToWorldPosition(Vector2d, bool) trouvée");
                var resultWithBool = geoToWorldMethod.Invoke(_mapboxMap, new object[] { latLng, false });

                if (resultWithBool is Vector3)
                {
                    worldPos = (Vector3)resultWithBool;
                    Debug.Log(string.Format("[MapManager.TryGeoToWorldPosition] SUCCÈS: GPS({0:F6}, {1:F6}) -> world({2})",
                        latitude, longitude, worldPos));
                    return true;
                }
                else
                {
                    Debug.LogError(string.Format("[MapManager.TryGeoToWorldPosition] Résultat inattendu: {0}",
                        resultWithBool != null ? resultWithBool.GetType().Name : "NULL"));
                }
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format("[MapManager.TryGeoToWorldPosition] Exception: {0}\nStackTrace: {1}",
                    e.Message, e.StackTrace));
            }

            return false;
        }
    }
}
