function tryLock(lockId, createdDate, ttl) {
    var collection = getContext().getCollection();
    var collectionLink = collection.getSelfLink();
    var response = getContext().getResponse();

    queryExistingLock();

    function queryExistingLock() {
        var query = 'select * from root r where r.id = "' + lockId + '"';
        var accept = collection.queryDocuments(collectionLink, query, {}, function (err, documents) {
            if (err) {
                throw err;
            }
            if (documents.length > 0) {
                var existingDocument = documents[0];
                response.setBody({
                    locked: false,
                    etag: existingDocument._etag,
                    createdDate: existingDocument.createdDate
                });
            } else {
                createNewLockRecord();
            }
        });
        if (!accept) throw "Unable to query for existing lock";
    }

    function createNewLockRecord() {
        let lockItem = {id: lockId, createdDate: createdDate, ttl: ttl};
        var accept = collection.createDocument(collectionLink, lockItem,
            function (err, createdDocument) {
                if (err) {
                    throw err;
                }
                response.setBody({
                    locked: true,
                    etag: createdDocument._etag,
                    createdDate: createdDocument.createdDate
                });
            });
        if (!accept) throw "Unable to create a lock";
    }
}