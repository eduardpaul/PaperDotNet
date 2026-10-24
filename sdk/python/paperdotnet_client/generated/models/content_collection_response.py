from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .content_resource_response import ContentResourceResponse
    from .effective_schema import EffectiveSchema

@dataclass
class ContentCollectionResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The canManage property
    can_manage: Optional[bool] = None
    # The canPublish property
    can_publish: Optional[bool] = None
    # The canReadDraft property
    can_read_draft: Optional[bool] = None
    # The canWrite property
    can_write: Optional[bool] = None
    # The resource property
    resource: Optional[ContentResourceResponse] = None
    # The schema property
    schema: Optional[EffectiveSchema] = None
    # The schemaRevisionId property
    schema_revision_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentCollectionResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentCollectionResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentCollectionResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .content_resource_response import ContentResourceResponse
        from .effective_schema import EffectiveSchema

        from .content_resource_response import ContentResourceResponse
        from .effective_schema import EffectiveSchema

        fields: dict[str, Callable[[Any], None]] = {
            "canManage": lambda n : setattr(self, 'can_manage', n.get_bool_value()),
            "canPublish": lambda n : setattr(self, 'can_publish', n.get_bool_value()),
            "canReadDraft": lambda n : setattr(self, 'can_read_draft', n.get_bool_value()),
            "canWrite": lambda n : setattr(self, 'can_write', n.get_bool_value()),
            "resource": lambda n : setattr(self, 'resource', n.get_object_value(ContentResourceResponse)),
            "schema": lambda n : setattr(self, 'schema', n.get_object_value(EffectiveSchema)),
            "schemaRevisionId": lambda n : setattr(self, 'schema_revision_id', n.get_str_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("canManage", self.can_manage)
        writer.write_bool_value("canPublish", self.can_publish)
        writer.write_bool_value("canReadDraft", self.can_read_draft)
        writer.write_bool_value("canWrite", self.can_write)
        writer.write_object_value("resource", self.resource)
        writer.write_object_value("schema", self.schema)
        writer.write_str_value("schemaRevisionId", self.schema_revision_id)
        writer.write_additional_data_value(self.additional_data)


