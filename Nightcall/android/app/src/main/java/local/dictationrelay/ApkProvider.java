package local.dictationrelay;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;

public final class ApkProvider extends ContentProvider {
    @Override public boolean onCreate() { return true; }
    @Override public String getType(Uri uri) {
        if ("/clip.png".equals(uri.getPath())) return "image/png";
        return "application/vnd.android.package-archive";
    }
    @Override public String[] getStreamTypes(Uri uri, String mimeTypeFilter) {
        if ("/clip.png".equals(uri.getPath())) return new String[] { "image/png" };
        return super.getStreamTypes(uri, mimeTypeFilter);
    }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        String path = uri.getPath();
        File file;
        if ("/update.apk".equals(path)) file = new File(getContext().getCacheDir(), "update.apk");
        else if ("/clip.png".equals(path)) file = new File(getContext().getCacheDir(), "clip.png");
        else throw new FileNotFoundException("Not found");
        if (!file.exists()) throw new FileNotFoundException("File missing");
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sortOrder) {
        if (!"/clip.png".equals(uri.getPath())) return null;
        File file = new File(getContext().getCacheDir(), "clip.png");
        if (!file.exists()) return null;
        String[] cols = projection != null ? projection : new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE };
        MatrixCursor cursor = new MatrixCursor(cols, 1);
        Object[] row = new Object[cols.length];
        for (int i = 0; i < cols.length; i++) {
            if (OpenableColumns.DISPLAY_NAME.equals(cols[i])) row[i] = "clipboard.png";
            else if (OpenableColumns.SIZE.equals(cols[i])) row[i] = file.length();
        }
        cursor.addRow(row);
        return cursor;
    }
    @Override public Uri insert(Uri u, ContentValues v) { return null; }
    @Override public int delete(Uri u, String s, String[] a) { return 0; }
    @Override public int update(Uri u, ContentValues v, String s, String[] a) { return 0; }
}
